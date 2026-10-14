# Reportes avanzados 1.1.0

Esta versión agrega reportes de saldos y movimientos de cuentas por cobrar, además del desglose de cobros y reintegros por turno, método y moneda.

## Requisitos

- POS anfitrión actualizado a Desktop API 1.4.
- Permiso de operador `ViewReports` para consultar las fuentes nuevas.
- No requiere migración de base de datos.

## Reportes nuevos

- **Saldos por cobrar:** saldos positivos por cliente en USD, equivalente VES a tasa vigente, límite, crédito disponible y último movimiento.
- **Movimientos de cartera:** cargos y abonos entre fechas UTC, hasta 92 días, con importes originales, tasa, referencia, venta y operador.
- **Cobros y reintegros por caja:** totales agrupados por turno/caja, operación, método y moneda. El período usa la apertura del turno.

Las vistas personales, búsqueda y exportaciones CSV/XLSX del visor se conservan. El reporte de cartera usa el permiso actual `ViewReports`, por lo que todo operador que lo tenga puede consultar la cartera consolidada. El equivalente VES se calcula a tasa vigente; no es una reconstrucción histórica.

## Instalar

El ZIP firmado no está incluido todavía en este directorio. Para generarlo, hace falta el certificado privado correspondiente a la clave pública registrada para el publicador `pos-warehouses-dev-2026`. No se debe sustituir por un certificado distinto sin actualizar y verificar la confianza de módulos en el POS.

Con ese certificado instalado en `Cert:\CurrentUser\My`, ejecuta desde la raíz del repositorio:

```powershell
.\scripts\Build-ReportModulePackage.ps1 -CertificateThumbprint "<huella del certificado del publicador>"
```

El script crea `artifacts\modules\advanced-reports\1.1.0.zip`. Verifica el paquete con `scripts\Test-SignedModulePackage.ps1`, y luego copia aquí el ZIP firmado, la clave pública y su SHA-256 antes de distribuirlo.

La guía de actualización para operadores está en [`docs/manuales/REPORTES-AVANZADOS-1.1.0.md`](../../../docs/manuales/REPORTES-AVANZADOS-1.1.0.md).
