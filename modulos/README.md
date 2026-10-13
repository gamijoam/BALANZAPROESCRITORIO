# Catálogo de módulos distribuibles

Este directorio conserva los paquetes firmados que se entregan para instalar en el POS. El código fuente vive en `src/Modules`; aquí se guardan versiones publicables e instrucciones específicas.

## Módulos disponibles

| ID | Nombre | Versión | Release |
|---|---|---:|---|
| thermal-receipts | Tickets térmicos | 1.0.0 | [Paquete, requisitos e instrucciones](thermal-receipts/1.0.0/README.md) |
| `warehouses` | Almacenes y traslados | 1.3.3 | [Paquete e instrucciones](warehouses/1.3.3/README.md) |
| `advanced-reports` | Reportes avanzados | 1.0.2 | [Paquete, requisitos e instrucciones](advanced-reports/1.0.2/README.md) |

## Convenciones

- Ruta: `modulos/<id-interno>/<versión>/`.
- Incluye el ZIP firmado, clave **pública** del publicador y README con requisitos, cambios, instalación, actualización y SHA-256.
- No incluyas claves privadas, tokens, certificados privados, credenciales, respaldos ni datos de comercios.
- No reemplaces releases anteriores: cada versión instalada debe poder localizarse después.
- El `id-interno` debe coincidir con `id` en `module.manifest.json`; no se obtiene del nombre del ZIP.
- Verifica la firma y los hashes con `scripts/Test-SignedModulePackage.ps1` antes de agregar o distribuir un release.
