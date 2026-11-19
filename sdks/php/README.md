# LatticeLog SDK for PHP (P21, alpha)

Structured records in a **durable local spool shared** by every PHP process of the application, shipped to
`latticelog-server` with ingest protocol v1. PHP ≥ 8.1 with `json`, `zlib` and `mbstring`; `curl` is optional.

```php
require 'vendor/autoload.php';   // or: require 'sdks/php/src/autoload.php';

$log = new LatticeLog\Logger('/var/spool/shop', [
    'endpoint' => 'https://logs:7400', 'token' => getenv('LATTICELOG_TOKEN'),
    'service' => 'shop', 'namespace' => 'acme/prod',
]);
$log->operation('checkout', function () use ($log) {
    $log->error('payment failed for {order}', ['order' => 991, 'amount' => 19.99, 'exception' => $e ?? null]);
}, 'op-991');
```

- **Many processes**: every write takes a file lock, so all requests get unique, dense sequences. Tested with 6
  concurrent processes.
- **Shipping**: at the end of the request (`ship_on_shutdown`, 2 s by default, after `fastcgi_finish_request`) or
  from a worker or cron job: `LATTICELOG_TOKEN=... php bin/latticelog-ship.php /var/spool/shop https://logs:7400 acme/prod`.
  Only one process ships at a time.
- **Guarantees**: the same as the Python SDK. Server-side deduplication, nothing unconfirmed is deleted, and when the
  spool is full new records are rejected and counted.
- **Types**: `int` → Int64, `float` → Double (NaN and infinities included), `DateTimeInterface` → Timestamp,
  associative arrays → ordered map, lists → array. Strings that are not valid UTF-8 travel as bytes.

Integration tests: `tests/LatticeLog.Tests/SdkTests.cs` (`tests/sdk/php_smoke.php`).
