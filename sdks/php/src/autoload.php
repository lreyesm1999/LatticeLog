<?php
// Minimal PSR-4 autoloader for use without Composer: require __DIR__ . '/src/autoload.php';
spl_autoload_register(static function (string $class): void {
    $prefix = 'LatticeLog\\';
    if (str_starts_with($class, $prefix)) {
        $file = __DIR__ . '/LatticeLog/' . str_replace('\\', '/', substr($class, strlen($prefix))) . '.php';
        if (is_file($file)) {
            require $file;
        }
    }
});
