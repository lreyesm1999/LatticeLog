<?php
// Cron/worker shipper: php latticelog-ship.php <spool-dir> <endpoint> [<namespace>] [<seconds>]
// The token is read from LATTICELOG_TOKEN (never from the command line).
declare(strict_types=1);
require __DIR__ . '/../src/autoload.php';

[$spool, $endpoint] = [$argv[1] ?? null, $argv[2] ?? null];
$token = getenv('LATTICELOG_TOKEN');
if ($spool === null || $endpoint === null || $token === false) {
    fwrite(STDERR, "usage: LATTICELOG_TOKEN=... php latticelog-ship.php <spool-dir> <endpoint> [<namespace>] [<seconds>]\n");
    exit(2);
}
$sender = new LatticeLog\Sender(new LatticeLog\Spool($spool), $endpoint, $token, $argv[3] ?? null);
$committed = $sender->ship((float)($argv[4] ?? 55));
echo $committed === null ? "another process is shipping\n" : "committed through $committed" . ($sender->lastError ? " (last error: {$sender->lastError})" : '') . "\n";
