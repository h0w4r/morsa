# Alcance y niveles de actividad

El alcance es una frontera de autorización en runtime, no un filtro del reporte.
Morsa lo comprueba antes de cada operación activa y de cada redirect/destino derivado.

| Modo | Operaciones típicas | Efecto de red |
|---|---|---|
| `passive` | parsing local, datasets importados, índices | no requiere contacto directo |
| `active` | HTTP, DNS, TLS, banner y crawler acotados | contacto dentro del presupuesto |
| `aggressive` | candidatos de backup y fuzzing explícito | mayor densidad, presupuesto separado |

`MaximumMode` es un techo. Una entrada pasiva no sirve para comandos activos; una
entrada activa no autoriza actividad agresiva.

```bash
morsa scope add example.org --kind domain --max-mode active
morsa scope add api.example.org --kind host --max-mode active
morsa scope add https://example.org/public/ --kind url --max-mode active
morsa scope add 203.0.113.10 --kind ip --max-mode active
morsa scope add 203.0.113.0/28 --kind cidr --max-mode active
```

Usa la entrada más estrecha. `host` no autoriza hermanos; `url` limita esquema y
ruta; `ip/cidr` aplican a direcciones literales. Revisa `scope list` en vez de
suponer wildcards.

Cada salto HTTP se canonicaliza y valida. Se rechaza userinfo; se normalizan host
y puerto; redes privadas/link-local/loopback se bloquean salvo configuración
explícita; un redirect no hereda alcance solo por venir de una URL autorizada. La
resolución DNS también se valida para reducir rebinding.

Un proxy jamás amplía alcance. SOCKS5h resuelve remotamente, pero el destino lógico
pasa alcance/SSRF antes de crear el túnel.

Patrón recomendado: workspace por autorización, entradas mínimas, conservar
`scope list --json`, ejecutar pasivo primero, elevar explícitamente y revisar
`NetworkAttempt`. Ante rechazo corrige destino/modo; `0.0.0.0/0` no es una llave
maestra, es una forma elegante de borrar el propósito del control.
