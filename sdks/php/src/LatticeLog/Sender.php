<?php
declare(strict_types=1);

namespace LatticeLog;

/**
 * Ships the spool with the ingestion protocol v1 (specs/ingest-protocol.md): JSONL body, gzip, idempotent retries.
 * PHP has no long-lived thread: call ship() at the end of a request (the Logger can do it on shutdown with a time
 * budget) or from a cron/CLI worker (bin/latticelog-ship.php). Only one process ships at a time.
 */
final class Sender
{
    public ?string $lastError = null;

    public function __construct(private readonly Spool $spool, private readonly string $endpoint, private readonly string $token,
        private readonly ?string $namespace = null, private readonly int $batchRecords = 1000, private readonly string $ack = 'fsync',
        private readonly float $timeout = 30.0)
    {
    }

    /**
     * Sends batches until the spool is empty or $budgetSeconds elapsed. Returns the committed sequence, or null if
     * another process is shipping. Network errors leave everything in the spool for the next call.
     */
    public function ship(float $budgetSeconds = 2.0): ?int
    {
        $deadline = microtime(true) + $budgetSeconds;
        return $this->spool->exclusiveShip(function () use ($deadline): int {
            $max = $this->batchRecords;
            $attempt = 0;
            while (microtime(true) < $deadline) {
                $after = $this->spool->committedThrough();
                $records = $this->spool->readAfter($after, $max);
                if ($records === []) {
                    return $after;
                }
                $run = [$records[0]];
                for ($i = 1, $n = count($records); $i < $n && $records[$i]['seq_n'] === $run[count($run) - 1]['seq_n'] + 1; $i++) {
                    $run[] = $records[$i];
                }
                $first = $run[0]['seq_n'];
                [$status, $payload, $retryAfter] = $this->post(self::envelope($this->spool->producerId, $first, $run, $first !== $after + 1));
                if ($status === 200 && (($payload['ack'] ?? '') === 'FsyncConfirmed' || $this->ack === 'os')) {
                    $attempt = 0;
                    $max = $this->batchRecords;
                    $committed = (int)$payload['committed_through'];
                    if ($committed > $after) {
                        $this->spool->acknowledge($committed);
                    }
                    $this->lastError = null;
                } elseif ($status === 409 && ($payload['error'] ?? '') === 'sequence_gap') {
                    $this->spool->acknowledge((int)$payload['committed_through']);
                } elseif ($status === 413 && $max > 1) {
                    $max = max(1, intdiv(count($run), 2));
                } elseif ($status === 0 || $status === 429 || $status >= 500 || $status === 200) {
                    $this->lastError = $payload['error'] ?? "HTTP $status";
                    $attempt++;
                    $cap = min(5.0, 0.2 * (2 ** min($attempt, 10)));
                    $delay = max($retryAfter ?? 0.0, mt_rand() / mt_getrandmax() * $cap); // full jitter
                    if (microtime(true) + $delay >= $deadline) {
                        break;
                    }
                    usleep((int)($delay * 1_000_000));
                } else {
                    $this->lastError = "HTTP $status " . ($payload['error'] ?? '') . ': ' . ($payload['message'] ?? '');
                    throw new \RuntimeException('LatticeLog: server rejected the batch (' . $this->lastError . ')');
                }
            }
            return $this->spool->committedThrough();
        });
    }

    /** @param list<array<string,mixed>> $run */
    public static function envelope(string $producerId, int $first, array $run, bool $gapAcknowledged): string
    {
        $lines = [Values::json(['type' => 'header', 'format' => 'latticelog.jsonl', 'version' => 1, 'store_id' => $producerId])];
        $refs = [];
        $contextRef = function (?array $ctx) use (&$contextRef, &$refs, &$lines): int {
            if ($ctx === null) {
                return 0;
            }
            $key = Values::json($ctx);
            if (isset($refs[$key])) {
                return $refs[$key];
            }
            $parent = $contextRef($ctx['parent'] ?? null);
            $ref = count($refs) + 1;
            $refs[$key] = $ref;
            $line = ['type' => 'context', 'ref' => $ref];
            if ($parent) {
                $line['parent'] = $parent;
            }
            $line['kind'] = $ctx['kind'] ?? 'Custom';
            if (isset($ctx['name'])) {
                $line['name'] = $ctx['name'];
            }
            $line['attributes'] = $ctx['attributes'] ?? [];
            $lines[] = Values::json($line);
            return $ref;
        };
        foreach ($run as $r) {
            $ref = $contextRef($r['ctx'] ?? null);
            unset($r['ctx'], $r['seq_n']);
            if ($ref) {
                $r['context'] = $ref;
            }
            $lines[] = Values::json($r);
        }
        $payload = gzencode(implode("\n", $lines) . "\n", 1);
        $count = count($run);
        return 'LLIB' . chr(1) . chr(1) . chr(1) . chr($gapAcknowledged ? 1 : 0)
            . hex2bin($producerId)
            . pack('P', $first) . pack('P', $first + $count - 1) . pack('V', $count) . pack('V', crc32($payload))
            . $payload;
    }

    /** @return array{int, array<string,mixed>, ?float} status (0 = network error), JSON body, Retry-After seconds */
    private function post(string $body): array
    {
        $headers = ['Authorization: Bearer ' . $this->token, 'Content-Type: application/vnd.latticelog.batch', 'LatticeLog-Ack: ' . $this->ack];
        if ($this->namespace !== null) {
            $headers[] = 'LatticeLog-Namespace: ' . $this->namespace;
        }
        $url = rtrim($this->endpoint, '/') . '/v1/ingest';
        if (function_exists('curl_init')) {
            $retryAfter = null;
            $ch = curl_init($url);
            curl_setopt_array($ch, [CURLOPT_POST => true, CURLOPT_POSTFIELDS => $body, CURLOPT_HTTPHEADER => $headers,
                CURLOPT_RETURNTRANSFER => true, CURLOPT_TIMEOUT_MS => (int)($this->timeout * 1000),
                CURLOPT_HEADERFUNCTION => function ($ch, $line) use (&$retryAfter) {
                    if (stripos($line, 'Retry-After:') === 0) {
                        $retryAfter = (float)trim(substr($line, 12));
                    }
                    return strlen($line);
                }]);
            $resp = curl_exec($ch);
            $status = $resp === false ? 0 : (int)curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
            $error = $resp === false ? 'network: ' . curl_error($ch) : null;
            curl_close($ch);
            $payload = $resp === false ? ['error' => $error] : (json_decode((string)$resp, true) ?: []);
            return [$status, $payload, $retryAfter];
        }
        $ctx = stream_context_create(['http' => ['method' => 'POST', 'header' => implode("\r\n", $headers), 'content' => $body,
            'timeout' => $this->timeout, 'ignore_errors' => true]]);
        $resp = @file_get_contents($url, false, $ctx);
        if ($resp === false) {
            return [0, ['error' => 'network error'], null];
        }
        $status = 0;
        $retryAfter = null;
        foreach ($http_response_header ?? [] as $h) {
            if (preg_match('#^HTTP/\S+ (\d{3})#', $h, $m)) {
                $status = (int)$m[1];
            } elseif (stripos($h, 'Retry-After:') === 0) {
                $retryAfter = (float)trim(substr($h, 12));
            }
        }
        return [$status, json_decode($resp, true) ?: [], $retryAfter];
    }
}
