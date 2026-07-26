# Modelo de amenazas

## Activos y adversarios

Se protegen alcance/modo, artefactos SHA-256, procedencia de evidencia, SQLite,
reportes, credenciales/cookies, host del operador y consumidores de releases.
Adversarios: origen remoto con DNS/redirect/TLS, proxy comprometido, archivo hostil,
plugin malicioso, usuario local, dependencia/build comprometido y error operativo.

| Límite | Amenaza | Control | Prueba |
|---|---|---|---|
| URL/DNS/socket | SSRF, rebinding, escape por redirect | canonicalización, alcance por salto, redes privadas y presupuestos | tests SSRF/redirect/rebinding |
| Proxy | fuga de clave/header/cookie | `secret_ref`, handlers/cookies separados, auth aislada, TLS | capturas mock proxy/origen |
| Rotación | retry infinito/rate multiplicado | attempts/rotations, rate global, cooldown/circuit | 403/429/challenge/timeout |
| ZIP/XML | traversal, ZIP bomb, XXE | rutas seguras, bytes/entries, DTD prohibido | corpus adversarial/fuzz |
| Parser | crash/hang/recursos | proceso separado, timeout, sandbox, protocolo acotado | pruebas crash/hang |
| Plugin | traversal/sustitución/hang | staging, manifest/hash, env limpio, caps | paquetes/plugins malformados |
| MCP | corrupción stdout/escape de ruta | logs stderr, canonicalización, reports confinados | tests de protocolo/rutas |
| SQLite | commit parcial/falso éxito | transacciones, WAL, tasks durables | interrupción/reanudación |
| Release | manipulación/supply chain | audit, CodeQL, SHA, SBOM, attest OIDC | instalación limpia/verificación |

## Invariantes

- Cada intento/redirect pasa alcance antes de salir.
- El número de proxies no multiplica el presupuesto del objetivo.
- `Proxy-Authorization` no cruza al origen; TLS valida; fallback directo es explícito.
- SHA-256 identifica contenido; magic/MIME antecede extractor.
- Parsers no ejecutan macros, scripts, links u objetos.
- Los límites de descompresión/parsing/retry fallan cerrado.
- Evidencia conserva localizador a la estructura fuente.
- Un fallo parcial no se convierte en éxito total.

## Riesgo residual

Quien lee el workspace puede leer evidencia no redactada; un proxy ve tráfico no
protegido end-to-end; DNS remoto depende de su resolver; dependencias nativas pueden
tener fallas desconocidas; permisos de plugin no equivalen a MAC en cada distro;
providers cambian formatos/términos. Acceso privado, aggressive, fallback directo,
credenciales, uploads y degradación de sandbox requieren decisión visible.
