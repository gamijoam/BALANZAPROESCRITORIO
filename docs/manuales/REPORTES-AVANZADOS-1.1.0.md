# Reportes avanzados 1.1.0

Esta versión incorpora cartera por cobrar y un resumen de cobros/reintegros por turno.

## Reportes incluidos

- **Saldos por cobrar:** una fila por cliente activo con deuda positiva; saldo contable USD, equivalente VES a la tasa vigente, límite, crédito disponible y fecha del último movimiento.
- **Movimientos de cuentas por cobrar:** cargos y abonos dentro del período UTC seleccionado, con moneda e importe originales, tasa aplicada, referencia, venta, operador y terminal. El período máximo es de 92 días.
- **Cobros y reintegros por caja:** totales agrupados por turno, operación, método y moneda. La fecha selecciona turnos por su hora de apertura; no representa la fecha individual de cada pago.

Los reportes de cartera se autorizan con `ViewReports`, que permite consultar toda la cartera consolidada. La conversión a VES es orientativa y usa la tasa vigente; el saldo contable se mantiene en USD. No se calcula antigüedad vencida ni se asignan abonos a cargos.

## Actualización

Actualiza el POS anfitrión a Desktop API 1.4 y luego instala el paquete firmado `advanced-reports-1.1.0.zip` desde **Administración → Módulos**. No se agrega una migración de base de datos. Se conservan las vistas personales y las exportaciones CSV/XLSX del visor.

El paquete firmado y su clave pública se incorporan a este directorio cuando se construyan con el certificado privado del publicador. No se almacena la clave privada en el repositorio.

Para construirlo desde la raíz del repositorio, el publicador debe tener disponible su certificado privado en `Cert:\CurrentUser\My`:

```powershell
.\scripts\Build-ReportModulePackage.ps1 -CertificateThumbprint "<huella del certificado del publicador>"
```

La compilación genera el ZIP firmado en `artifacts\modules\advanced-reports\1.1.0.zip`. Antes de distribuirlo, cópialo y su clave pública a este directorio, verifica firma y hashes con `scripts\Test-SignedModulePackage.ps1`, y actualiza aquí el SHA-256.
