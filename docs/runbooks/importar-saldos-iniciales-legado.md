# Importar saldos iniciales del sistema anterior

## Objetivo

Registrar los saldos pendientes agrupados por cliente desde `novedades_stage` como saldos iniciales auditados en el POS. El importador no crea operadores, ventas históricas ni movimientos directamente en PostgreSQL: consulta el origen y usa el endpoint HTTP del servidor para cada apertura.

## Criterios de aceptación

1. La vista previa debe mostrar 81 clientes y un total de 1260.0513 USD antes de permitir la carga.
2. Cada cliente de origen debe tener exactamente un cliente destino activo, en USD, y su cuenta de cobro debe estar vacía.
3. Una coincidencia por documento debe ser única. Las coincidencias resueltas por nombre y teléfono aparecen destacadas; una coincidencia sin documento ni teléfono se identifica expresamente para revisión. Si no hay una coincidencia única, el importador se detiene.
4. Sin el parámetro `-Apply`, el script solo consulta y muestra la vista previa. Con `-Apply`, solicita usuario y PIN de forma interactiva y exige escribir `CARGAR 81` antes de hacer solicitudes de escritura.
5. Cada saldo se registra por `/api/v1/customers/{id}/receivables/opening-balance`, con importe decimal exacto, motivo de corte, clave idempotente estable, y autenticación existente. El certificado TLS debe coincidir con la huella protegida en el perfil local.
6. La repetición de la carga no duplica aperturas. El script acepta continuar solo si un movimiento previo coincide exactamente en cliente, importe, motivo y clave idempotente; cualquier otro movimiento bloquea la carga. Los errores detienen la ejecución y las solicitudes procesadas se pueden reintentar con la misma clave.
7. Al terminar, el script vuelve a consultar las cuentas y comprueba un cargo inicial por cliente, el saldo exacto y 81 eventos de auditoría.

## Ejecución

- Vista previa: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Import-LegacyReceivableOpeningBalances.ps1`
- Confirmar carga: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Import-LegacyReceivableOpeningBalances.ps1 -Apply`

El acceso se lee del perfil protegido de la instalación. El PIN del operador se solicita en pantalla y no se guarda. No ejecutar una carga si el resumen, los clientes resaltados o el destino de la API no son los esperados.

## Si la ejecución se interrumpe

El importador muestra cuántos saldos confirmó el servicio antes de comenzar la verificación final. Si aparece un error después de ese mensaje, no registres esos saldos manualmente: vuelve a ejecutar el importador actualizado. Antes de permitir reintentos, revisa que cada apertura existente coincida con el cliente, el importe, el motivo y la clave idempotente esperados. El servicio reconocerá las mismas claves y no duplicará aperturas.
