<?php
declare(strict_types=1);

namespace LatticeLog;

/**
 * Local durable spool shared by every PHP process of an application (same file format as the Python SDK). Appends
 * take an exclusive lock, so concurrent requests get dense, unique local sequences. Files are named by fixed sequence
 * ranges; only files fully acknowledged by the server are deleted, never unacknowledged data.
 */
final class Spool
{
    public const FILE_RECORDS = 10_000;

    public readonly string $producerId;
    public int $dropped = 0;

    public function __construct(public readonly string $dir, private readonly int $maxBytes = 512 * 1024 * 1024, private readonly string $durability = 'balanced')
    {
        if (!in_array($durability, ['fast', 'balanced', 'durable'], true)) {
            throw new \InvalidArgumentException('durability must be fast, balanced or durable');
        }
        if (!is_dir($dir) && !@mkdir($dir, 0700, true) && !is_dir($dir)) {
            throw new \RuntimeException("cannot create spool directory $dir");
        }
        $this->producerId = $this->withLock('spool.lock', function (): string {
            $meta = $this->dir . '/producer.json';
            if (is_file($meta)) {
                return json_decode((string)file_get_contents($meta), true, 8, JSON_THROW_ON_ERROR)['producer_id'];
            }
            $id = bin2hex(random_bytes(16));
            self::atomicWrite($meta, json_encode(['producer_id' => $id], JSON_THROW_ON_ERROR));
            return $id;
        });
    }

    /** @template T @param callable():T $fn @return T */
    private function withLock(string $name, callable $fn, bool $blocking = true): mixed
    {
        $h = fopen($this->dir . '/' . $name, 'c');
        if ($h === false) {
            throw new \RuntimeException("cannot open lock $name");
        }
        try {
            if (!flock($h, $blocking ? LOCK_EX : LOCK_EX | LOCK_NB)) {
                return null;
            }
            try {
                return $fn();
            } finally {
                flock($h, LOCK_UN);
            }
        } finally {
            fclose($h);
        }
    }

    public static function rangeStart(int $seq): int
    {
        return intdiv($seq - 1, self::FILE_RECORDS) * self::FILE_RECORDS + 1;
    }

    public function path(int $first): string
    {
        return sprintf('%s/spool-%020d.jsonl', $this->dir, $first);
    }

    /** @return list<array{int,string}> sorted (first sequence, path) */
    public function files(): array
    {
        $out = [];
        foreach (glob($this->dir . '/spool-*.jsonl') ?: [] as $p) {
            $out[] = [(int)substr(basename($p), 6, 20), $p];
        }
        usort($out, fn($a, $b) => $a[0] <=> $b[0]);
        return $out;
    }

    public function committedThrough(): int
    {
        $p = $this->dir . '/cursor.json';
        return is_file($p) ? (int)json_decode((string)file_get_contents($p), true)['committed_through'] : 0;
    }

    /** Next sequence: state file, else the last complete line of the newest file (a torn line is cut off). */
    private function nextSeq(): int
    {
        $state = $this->dir . '/state.json';
        $next = max(1, $this->committedThrough() + 1);
        if (is_file($state)) {
            $next = max($next, (int)json_decode((string)file_get_contents($state), true)['next_seq']);
        }
        $files = $this->files();
        if ($files !== []) {
            [, $path] = $files[count($files) - 1];
            $data = (string)file_get_contents($path);
            $good = strrpos($data, "\n");
            $good = $good === false ? 0 : $good + 1;
            if ($good !== strlen($data)) {
                $h = fopen($path, 'r+');
                ftruncate($h, $good);
                fclose($h);
                $data = substr($data, 0, $good);
            }
            $lines = $data === '' ? [] : explode("\n", rtrim($data, "\n"));
            if ($lines !== []) {
                $next = max($next, json_decode($lines[count($lines) - 1], true)['seq_n'] + 1);
            }
        }
        return $next;
    }

    public function size(): int
    {
        $n = 0;
        foreach ($this->files() as [, $p]) {
            $n += (int)@filesize($p);
        }
        return $n;
    }

    /** @param array<string,mixed> $record @return int|null the local sequence, or null when refused (spool full) */
    public function append(array $record): ?int
    {
        return $this->withLock('spool.lock', function () use ($record): ?int {
            if ($this->maxBytes > 0 && $this->size() >= $this->maxBytes) {
                $this->dropped++;
                return null;
            }
            $seq = $this->nextSeq();
            $record['seq'] = (string)$seq;
            $record['seq_n'] = $seq;
            $h = fopen($this->path(self::rangeStart($seq)), 'ab');
            fwrite($h, Values::json($record) . "\n");
            if ($this->durability !== 'fast') {
                fflush($h);
            }
            if ($this->durability === 'durable' && function_exists('fsync')) {
                fsync($h);
            }
            fclose($h);
            self::atomicWrite($this->dir . '/state.json', json_encode(['next_seq' => $seq + 1]), $this->durability === 'durable');
            return $seq;
        });
    }

    /** @return list<array<string,mixed>> up to $max records with seq > $after, in order */
    public function readAfter(int $after, int $max): array
    {
        $out = [];
        $files = $this->files();
        foreach ($files as $i => [$first, $path]) {
            if (isset($files[$i + 1]) && $files[$i + 1][0] - 1 <= $after) {
                continue;
            }
            $h = fopen($path, 'rb');
            while (($line = fgets($h)) !== false) {
                if (!str_ends_with($line, "\n")) {
                    break; // being written
                }
                $r = json_decode($line, true, 512, JSON_THROW_ON_ERROR);
                if ($r['seq_n'] <= $after) {
                    continue;
                }
                $out[] = $r;
                if (count($out) >= $max) {
                    fclose($h);
                    return $out;
                }
            }
            fclose($h);
        }
        return $out;
    }

    public function acknowledge(int $committed): void
    {
        self::atomicWrite($this->dir . '/cursor.json', json_encode(['committed_through' => $committed]));
        $this->withLock('spool.lock', function () use ($committed): void {
            $files = $this->files();
            foreach ($files as $i => [$first, $path]) {
                // Range files: a range is complete when it is full or a later range exists.
                $last = isset($files[$i + 1]) ? $files[$i + 1][0] - 1 : $first + self::FILE_RECORDS - 1;
                if ($last <= $committed) {
                    @unlink($path);
                }
            }
        });
    }

    /** Runs $fn only if no other process is shipping right now. */
    public function exclusiveShip(callable $fn): mixed
    {
        return $this->withLock('ship.lock', $fn, blocking: false);
    }

    public static function atomicWrite(string $path, string $text, bool $fsync = true): void
    {
        $tmp = $path . '.' . bin2hex(random_bytes(4)) . '.tmp';
        $h = fopen($tmp, 'wb');
        fwrite($h, $text);
        fflush($h);
        if ($fsync && function_exists('fsync')) {
            fsync($h);
        }
        fclose($h);
        rename($tmp, $path);
    }
}
