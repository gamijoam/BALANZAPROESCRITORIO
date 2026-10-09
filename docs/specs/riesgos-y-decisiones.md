# Riesgos y decisiones pendientes

| Tema | Base propuesta | Pendiente / responsable de validar |
|---|---|---|
| Impuestos, comprobantes, SENIAT | Configurables; no afirmar factura fiscal sin validación | Asesor legal/contable y fabricante autorizado antes de venta comercial |
| Moneda y redondeo | `decimal`/`numeric`, tasa y snapshots por venta | Moneda de referencia, escala por moneda, vuelto y redondeo por método de pago |
| Tasa BCV | Registro manual autorizado inicialmente | Fuente permitida, horario, vigencia, permisos y tratamiento de tasas desactualizadas |
| Fiados | Libro de saldo con abonos inmutables | Límites, roles, mora, reportes y correcciones |
| Stock negativo | Desactivado por defecto | Excepciones y permiso requerido |
| Cajas sin servidor | No confirman ventas en v1 | Procedimiento de contingencia manual; modo sincronizado es alcance futuro separado |
| Periféricos | Adaptadores y lista de compatibilidad | Modelos reales de impresora, escáner, gaveta, balanza/fiscal |
| Autenticación y LAN | Servicio como frontera de seguridad | TLS, certificados, emparejamiento, revocación y política de PIN |
| Instalador | Primer paquete Windows x64 framework-dependent: servidor+POS local; PostgreSQL externo | Firma, actualización, desinstalación y rollback; falta validar en PC limpia |
| PostgreSQL | Base local; scripts detectan la versión instalada bajo Archivos de programa y usan el rol de aplicación | Matriz de versiones soportadas y servicio Windows; restauración debe probarse periódicamente |
| Licencia offline | Entitlements verificados por servicio | Gracia, transferencia por cambio de hardware, expiración y soporte |
| CI/repositorio | Pipeline Windows y rama protegida | Confirmar proveedor/repositorio; pipeline propuesto usa GitHub Actions |

## Riesgos principales

1. Una regla fiscal incorrecta puede producir comprobantes inválidos; el gate legal y contable es independiente de las pruebas de software.
2. Reintentos o concurrencia mal resueltos pueden duplicar ventas o dejar stock/saldos inconsistentes; requieren pruebas de integración con PostgreSQL.
3. Cortes de LAN/energía y fallos de impresora exigen distinguir venta confirmada, pago pendiente e impresión pendiente.
4. La diversidad de hardware impide prometer compatibilidad sin pruebas con modelos concretos.
5. Migraciones sin restore probado pueden hacer irrecuperables datos del comercio.
