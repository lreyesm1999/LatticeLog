<?php
// Driven by tests/LatticeLog.Tests/SdkTests.cs.
//   php php_smoke.php <sdk-src> offline <spool>                    write 300 records, no server
//   php php_smoke.php <sdk-src> ship <spool> <endpoint> <token>    write 200 more, ship everything
//   php php_smoke.php <sdk-src> burst <spool> <worker>             100 records from one of several concurrent processes
declare(strict_types=1);
require $argv[1] . '/autoload.php';

use LatticeLog\Logger;

[$mode, $spool] = [$argv[2], $argv[3]];
if ($mode === 'offline') {
    $log = new Logger($spool, ['service' => 'php-shop', 'durability' => 'durable']);
    for ($i = 0; $i < 300; $i++) { $log->info('order {i}', ['i' => $i]); }
    exit(0);
}
if ($mode === 'burst') {
    $log = new Logger($spool, ['service' => 'php-shop']);
    for ($i = 0; $i < 100; $i++) { $log->info('burst', ['worker' => (int)$argv[4], 'n' => $i]); }
    exit(0);
}
[$endpoint, $token] = [$argv[4], $argv[5]];
$log = new Logger($spool, ['endpoint' => $endpoint, 'token' => $token, 'service' => 'php-shop', 'namespace' => 'acme/prod', 'ship_on_shutdown' => 0]);
for ($i = 300; $i < 500; $i++) {
    if ($i === 300) {
        $log->operation('checkout', function () use ($log, $i) {
            $inner = new RuntimeException('sku');
            $e = new LogicException('payment failed', 0, $inner);
            $log->error('special {i}', ['i' => $i, 'exception' => $e, 'ratio' => 1.0, 'nan' => NAN, 'raw' => "\x00\xff",
                'when' => new DateTimeImmutable('2026-01-02T03:04:05.123456Z'), 'nested' => ['a' => [1, 2.5, 'x']],
                'unicode' => 'héllo 世界 🎉', 'trace_id' => '4BF92F3577B34DA6A3CE929D0E0E4736']);
        }, 'op-300', ['cart' => 7]);
    } else {
        $log->info('order {i}', ['i' => $i]);
    }
}
$ok = $log->flush(30);
echo $ok ? "shipped\n" : 'not shipped: ' . $log->sender()?->lastError . "\n";
exit($ok ? 0 : 1);
