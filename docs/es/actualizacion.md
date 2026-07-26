# Guía de actualización

Actualiza CLI y helpers como un solo payload del mismo RID. Detén procesos, respalda
el workspace y prueba una copia antes de desplegar en todas las investigaciones.
No mezcles `morsa` nuevo con ParserHost/PluginHost/MCP viejos.

```bash
sqlite3 /cases/acme/morsa.db ".backup '/backups/acme-before-morsa-1.0.db'"
tar -C /cases -czf /backups/acme-artifacts-before-morsa-1.0.tar.gz \
  acme/artifacts acme/morsa.toml acme/.morsa
```

Sin `sqlite3`, copia `morsa.db`, `morsa.db-wal` y `morsa.db-shm` juntos únicamente
cuando no queden procesos. Verifica hash del backup.

```bash
sha256sum --check SHA256SUMS --ignore-missing
tar -xzf morsa-1.0.0-linux-x64.tar.gz
sudo bash ./morsa-1.0.0-linux-x64/install.sh --prefix /usr/local
morsa version
morsa doctor --project /cases/acme
morsa project status --project /cases/acme --json
```

Para DEB usa `apt install ./paquete.deb`; para RPM `dnf upgrade ./paquete.rpm`.
En OCI fija el digest; no bases producción en `latest`.

## De 0.x a 1.0

1. Exporta JSON/bundle con la versión vieja.
2. Respalda SQLite, artefactos, TOML y plugins.
3. Instala todos los helpers 1.0.
4. Ejecuta `doctor` y `project status --json` sin actividad de red.
5. Revisa normalización de scope y `MaximumMode`.
6. Revisa pools: fallback directo no es silencioso y secretos integrados usan `env:`.
7. Revalida plugins con `apiVersion: 1` y hash del entry point.
8. Reanaliza solo si las notas de release lo requieren.
9. Ejecuta `run resume` para pendientes.
10. Compara entidades/hallazgos/reportes y cada diagnóstico.

Rollback: detén procesos, restaura producto anterior, restaura base+artefactos+
config+plugins como conjunto si hubo migración incompatible, ejecuta doctor/status.
No abras una base migrada con binario viejo salvo compatibilidad documentada. Guarda
la copia fallida para diagnóstico; no entierres la única evidencia por entusiasmo.
