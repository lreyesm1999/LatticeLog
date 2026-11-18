# Despliegue en una sola máquina (P17-T15)

Una máquina, sin Kubernetes. `latticelog-server` recibe los datos de los forwarders y sirve las consultas.

## 1. Preparar

1. **Datos** en un disco con espacio de sobra y que no sea el del sistema si es posible. Dentro van
   `ns/<namespace>/` (un almacén por namespace) y `server.key` (la clave de los cursores; inclúyela en las copias de
   seguridad).
2. **Certificado TLS** en PFX. Fuera de loopback el servidor se niega a arrancar en HTTP plano, salvo con
   `--allow-insecure-http`, que no se recomienda.
3. **Tokens**. Para cada secreto:
   ```bash
   latticelog-server hash-token "<secreto largo y aleatorio>"
   ```
   y un `tokens.json` con un token por aplicación (`ingest`, limitado a su namespace) y uno por persona o equipo
   (`read`). Ver `specs/server-api.md` §2.

## 2. Arrancar

Binario:

```bash
export LATTICELOG_CERT_PASSWORD='…'
latticelog-server --data /srv/latticelog --tokens /etc/latticelog/tokens.json --listen https://0.0.0.0:7400 --cert /etc/latticelog/server.pfx
```

Contenedor (`deploy/Dockerfile`, imagen *chiseled* sin shell y con usuario no root):

```bash
docker build -f deploy/Dockerfile -t latticelog-server:dev .
docker run -d --name latticelog -p 7400:7400 -v /srv/latticelog:/data \
  -v /etc/latticelog/tokens.json:/config/tokens.json:ro -v /etc/latticelog/server.pfx:/run/secrets/server.pfx:ro \
  -e LATTICELOG_CERT_PASSWORD='…' latticelog-server:dev
```

Comprobar: `curl https://host:7400/v1/health` devuelve `{"status":"ok",…}`. Con un token `admin` también muestra el
detalle de cada namespace.

## 3. Aplicaciones

Las aplicaciones escriben en su almacén local y un `Forwarder` (paquete `LatticeLog.Ingest`) envía al servidor (ver
README). Si el servidor no está disponible, los datos esperan en el disco de la aplicación, que tiene su propia cuota.

## 4. Operación

- **Rotar tokens**: añade el token nuevo al fichero, cambia los clientes y quita el viejo. No hace falta reiniciar.
- **Actualizar**: parar, sustituir el binario o la imagen y arrancar. Al arrancar se recupera cada namespace y se
  reconstruye la deduplicación desde los datos, así que los forwarders pueden reintentar sin generar duplicados.
- **Copias de seguridad**: en caliente con `POST /v1/admin/backup?ns=…` (arrancar con `--backup-dir`) o
  `latticelog backup`; cada copia se verifica. Guarda también `server.key` y el fichero de tokens. Detalle y
  restauración en `docs/operations/runbook.md`.
- **Retención, compactación, verificación y borrado de privacidad**: en línea, sin parar, con `POST /v1/admin/…`
  (`specs/server-api.md` §5). Fuera de línea siguen existiendo `latticelog retention|compact|verify`.
- **Métricas**: `GET /metrics` (Prometheus, token admin). **Consola**: `https://host:7400/ui`. **Auditoría**:
  `data/audit/`.
- **Capacidad**: un hilo escritor por namespace. En la máquina de referencia, ~300–600k registros/s de ingestión con
  pocos productores (`reports/ingest-v1.md`). Las consultas pesadas compiten por CPU con la ingestión; si la
  ingestión es prioritaria, baja `MaxConcurrentQueries`.

## 5. Límites conocidos

- Réplica asíncrona opcional con failover manual (`docs/operations/replication.md`); sin clúster ni failover
  automático. Sin réplica, si se pierde el disco, se pierde lo que no esté en un backup ni en los almacenes locales de
  los productores.
- Imagen Docker no construida ni probada todavía en este proyecto.
