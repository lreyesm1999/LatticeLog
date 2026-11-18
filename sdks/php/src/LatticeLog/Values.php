<?php
declare(strict_types=1);

namespace LatticeLog;

/** Lossless value encoding of latticelog.jsonl v1 (specs/jsonl-exchange.md). */
final class Values
{
    public const SEVERITY = ['trace' => 1, 'debug' => 5, 'info' => 9, 'notice' => 9, 'warn' => 13, 'warning' => 13,
        'error' => 17, 'critical' => 21, 'alert' => 21, 'emergency' => 21, 'fatal' => 21];

    /** PHP value -> JSON-ready lossless form. 1 stays int64, 1.0 stays float64; invalid UTF-8 travels as bytes. */
    public static function encode(mixed $v): mixed
    {
        if ($v === null || is_bool($v) || is_int($v)) {
            return $v;
        }
        if (is_float($v)) {
            if (is_nan($v)) {
                return ['$f64bits' => strtoupper(bin2hex(pack('E', $v)))];
            }
            if (is_infinite($v)) {
                return ['$f64' => $v > 0 ? 'Infinity' : '-Infinity'];
            }
            return $v; // json_encode with JSON_PRESERVE_ZERO_FRACTION keeps "1.0"
        }
        if (is_string($v)) {
            return mb_check_encoding($v, 'UTF-8') ? $v : ['$bytes' => base64_encode($v)];
        }
        if ($v instanceof \DateTimeInterface) {
            return ['$ts' => self::rfc3339(self::nanos($v))];
        }
        if ($v instanceof \BackedEnum) {
            return $v->value;
        }
        if (is_array($v)) {
            if (array_is_list($v)) {
                return array_map([self::class, 'encode'], $v);
            }
            $pairs = [];
            foreach ($v as $k => $x) {
                $pairs[] = [self::key($k), self::encode($x)];
            }
            return ['$map' => $pairs];
        }
        if ($v instanceof \Stringable) {
            return (string)$v;
        }
        if (is_object($v)) {
            return self::encode(get_object_vars($v));
        }
        return (string)$v;
    }

    public static function key(int|string $k): string
    {
        $k = (string)$k;
        return mb_check_encoding($k, 'UTF-8') ? $k : mb_convert_encoding($k, 'UTF-8', 'UTF-8');
    }

    /** @param array<int|string,mixed> $attributes */
    public static function attributes(array $attributes): array
    {
        $out = [];
        foreach ($attributes as $k => $v) {
            $out[] = [self::key($k), self::encode($v)];
        }
        return $out;
    }

    public static function nanos(\DateTimeInterface $t): int
    {
        return (int)$t->format('U') * 1_000_000_000 + (int)$t->format('u') * 1_000;
    }

    public static function nowNanos(): int
    {
        // hrtime is monotonic; wall clock with microseconds is what we have in PHP.
        [$usec, $sec] = explode(' ', microtime());
        return (int)$sec * 1_000_000_000 + (int)round((float)$usec * 1_000_000) * 1_000;
    }

    public static function rfc3339(int $nanos): string
    {
        $sec = intdiv($nanos, 1_000_000_000);
        $frac = $nanos % 1_000_000_000;
        if ($frac < 0) { $frac += 1_000_000_000; $sec--; }
        return gmdate('Y-m-d\TH:i:s', $sec) . '.' . str_pad((string)$frac, 9, '0', STR_PAD_LEFT) . 'Z';
    }

    public static function json(mixed $v): string
    {
        return json_encode($v, JSON_PRESERVE_ZERO_FRACTION | JSON_UNESCAPED_SLASHES | JSON_THROW_ON_ERROR);
    }

    /** @return array<string,mixed> */
    public static function exception(\Throwable $e): array
    {
        $out = ['type' => get_class($e), 'message' => self::encode($e->getMessage()), 'stack' => self::encode($e->getTraceAsString())];
        if ($e->getPrevious() !== null) {
            $out['inner'] = [self::exception($e->getPrevious())];
        }
        return $out;
    }
}
