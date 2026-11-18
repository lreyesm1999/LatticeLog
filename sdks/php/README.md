# LatticeLog SDK para PHP (P21, alpha)

Registros estructurados en un **spool local duradero compartido** por todos los procesos PHP de la aplicación,
enviados a `latticelog-server` con el protocolo de ingestión v1. PHP ≥ 8.1 con `json`, `zlib` y `mbstring`; `curl` es
opcional.

```php
require 'vendor/autoload.php';   // o: require 'sdks/php/src/autoload.php';

$log = new LatticeLog\Logger('/var/spool/shop', [
    'endpoint' => 'https://logs:7400', 'token' => getenv('LATTICELOG_TOKEN'),
    'service' => 'shop', 'namespace' => 'acme/prod',
]);
$log->operation('checkout', function () use ($log) {
    $log->error('payment failed for {order}', ['order' => 991, 'amount' => 19.99, 'exception' => $e ?? null]);
}, 'op-991');
```

- **Varios procesos**: cada escritura toma un bloqueo de fichero, así que todas las peticiones obtienen secuencias
  únicas y densas. Probado con 6 procesos simultáneos.
- **Envío**: al terminar la petición (`ship_on_shutdown`, 2 s por defecto, después de `fastcgi_finish_request`) o
  desde un worker o cron: `LATTICELOG_TOKEN=... php bin/latticelog-ship.php /var/spool/shop https://logs:7400 acme/prod`.
  Solo envía un proceso a la vez.
- **Garantías**: las mismas que el SDK de Python. Deduplicación en el servidor, nada sin confirmar se borra, y si el
  spool se llena los registros nuevos se rechazan y se cuentan.
- **Tipos**: `int` → Int64, `float` → Double (NaN e infinitos incluidos), `DateTimeInterface` → Timestamp, arrays
  asociativos → mapa ordenado, listas → array. Las cadenas que no son UTF-8 válido viajan como bytes.

Pruebas de integración: `tests/LatticeLog.Tests/SdkTests.cs` (`tests/sdk/php_smoke.php`).
