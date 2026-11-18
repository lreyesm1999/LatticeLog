<?php
declare(strict_types=1);

namespace LatticeLog;

/**
 * LatticeLog SDK for PHP (P21). Writes structured records to a local durable spool shared by all PHP processes of the
 * application and ships them to latticelog-server (at shutdown with a time budget, or from a worker).
 *
 *   $log = new \LatticeLog\Logger('/var/spool/myapp', ['endpoint' => 'https://logs:7400', 'token' => getenv('LATTICELOG_TOKEN'),
 *                                 'service' => 'shop', 'namespace' => 'acme/prod']);
 *   $log->error('payment failed for {order}', ['order' => 991, 'amount' => 19.99]);
 */
final class Logger
{
    private readonly Spool $spool;
    private readonly ?Sender $sender;
    private readonly int $minLevel;
    /** @var list<array<string,mixed>> */
    private array $contexts = [];
    /** @var list<string> */
    private array $operations = [];
    private bool $shipping = false;

    /**
     * Options: endpoint, token, namespace, service, resource (array), durability (fast|balanced|durable),
     * min_level, max_spool_bytes, ship_on_shutdown (seconds, default 2.0; 0 = never), batch_records.
     * @param array<string,mixed> $options
     */
    public function __construct(string $spoolDir, array $options = [])
    {
        $this->spool = new Spool($spoolDir, (int)($options['max_spool_bytes'] ?? 512 * 1024 * 1024), (string)($options['durability'] ?? 'balanced'));
        $this->minLevel = Values::SEVERITY[$options['min_level'] ?? 'info'];
        $resource = $options['resource'] ?? [];
        if (isset($options['service'])) {
            $resource += ['service.name' => $options['service']];
        }
        if ($resource !== []) {
            $this->contexts[] = ['kind' => 'Resource', 'attributes' => Values::attributes($resource)];
        }
        $this->sender = isset($options['endpoint'], $options['token'])
            ? new Sender($this->spool, (string)$options['endpoint'], (string)$options['token'], $options['namespace'] ?? null, (int)($options['batch_records'] ?? 1000))
            : null;
        $budget = (float)($options['ship_on_shutdown'] ?? 2.0);
        if ($this->sender !== null && $budget > 0) {
            register_shutdown_function(function () use ($budget): void {
                if (function_exists('fastcgi_finish_request')) {
                    fastcgi_finish_request(); // answer the client first, ship afterwards
                }
                try { $this->sender?->ship($budget); } catch (\Throwable) { /* never break the request; data stays spooled */ }
            });
        }
    }

    public function spool(): Spool { return $this->spool; }
    public function sender(): ?Sender { return $this->sender; }

    /** @param array<string,mixed> $attributes */
    public function pushScope(?string $name, array $attributes = [], string $kind = 'Scope'): void
    {
        $ctx = ['kind' => $kind, 'attributes' => Values::attributes($attributes)];
        if ($name !== null) {
            $ctx['name'] = $name;
        }
        $parent = $this->contexts === [] ? null : $this->contexts[count($this->contexts) - 1];
        if ($parent !== null) {
            $ctx['parent'] = $parent;
        }
        $this->contexts[] = $ctx;
    }

    public function popScope(): void
    {
        array_pop($this->contexts);
    }

    /** Runs $fn inside an operation context (operation_id on every record written meanwhile). */
    public function operation(string $name, callable $fn, ?string $operationId = null, array $attributes = []): mixed
    {
        $id = $operationId ?? bin2hex(random_bytes(8));
        $this->pushScope($name, ['operation.name' => $name] + $attributes, 'Operation');
        $this->operations[] = $id;
        try {
            return $fn($id);
        } finally {
            array_pop($this->operations);
            $this->popScope();
        }
    }

    /**
     * @param array<string,mixed> $attributes special keys: 'exception' (Throwable), 'trace_id', 'span_id', 'event', 'time' (DateTimeInterface)
     * @return int|null local sequence, or null when filtered or refused (spool full)
     */
    public function log(string $level, ?string $message, array $attributes = []): ?int
    {
        $sev = Values::SEVERITY[strtolower($level)] ?? throw new \InvalidArgumentException("unknown level $level");
        if ($sev < $this->minLevel) {
            return null;
        }
        $time = $attributes['time'] ?? null;
        $record = ['type' => 'record', 'time' => Values::rfc3339($time instanceof \DateTimeInterface ? Values::nanos($time) : Values::nowNanos()), 'severity_number' => $sev];
        foreach (['event' => 'event', 'trace_id' => 'trace_id', 'span_id' => 'span_id'] as $k => $field) {
            if (isset($attributes[$k])) {
                $record[$field] = $k === 'event' ? (string)$attributes[$k] : strtolower((string)$attributes[$k]);
            }
        }
        if ($message !== null) {
            $record['body'] = Values::encode($message);
        }
        $exception = $attributes['exception'] ?? null;
        unset($attributes['time'], $attributes['event'], $attributes['trace_id'], $attributes['span_id'], $attributes['exception']);
        if ($attributes !== []) {
            $record['attributes'] = Values::attributes($attributes);
        }
        if ($this->operations !== []) {
            $record['operation_id'] = $this->operations[count($this->operations) - 1];
            if (count($this->operations) > 1) {
                $record['parent_operation_id'] = $this->operations[count($this->operations) - 2];
            }
        }
        if ($exception instanceof \Throwable) {
            $record['exception'] = Values::exception($exception);
        }
        if ($this->contexts !== []) {
            $record['ctx'] = $this->contexts[count($this->contexts) - 1];
        }
        return $this->spool->append($record);
    }

    public function debug(string $m, array $a = []): ?int { return $this->log('debug', $m, $a); }
    public function info(string $m, array $a = []): ?int { return $this->log('info', $m, $a); }
    public function warning(string $m, array $a = []): ?int { return $this->log('warn', $m, $a); }
    public function error(string $m, array $a = []): ?int { return $this->log('error', $m, $a); }
    public function critical(string $m, array $a = []): ?int { return $this->log('fatal', $m, $a); }

    /** Ships now (blocking up to $budgetSeconds). Returns true when everything written so far is acknowledged. */
    public function flush(float $budgetSeconds = 10.0): bool
    {
        if ($this->sender === null) {
            return true;
        }
        $this->sender->ship($budgetSeconds);
        return $this->spool->committedThrough() >= $this->lastSequence();
    }

    private function lastSequence(): int
    {
        $state = $this->spool->dir . '/state.json';
        return is_file($state) ? (int)json_decode((string)file_get_contents($state), true)['next_seq'] - 1 : 0;
    }
}
