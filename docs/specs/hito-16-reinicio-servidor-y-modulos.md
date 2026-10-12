# Hito 16: reinicio confiable del servicio y activación de módulos

## Criterios de aceptación

1. Al abrir `Iniciar POS`, la aplicación identifica el `Pos.Server.exe` que responde en el puerto configurado. Si pertenece a una carpeta anterior o su ejecutable fue actualizado mientras seguía ejecutándose, lo detiene y arranca el servidor junto a la versión abierta.
2. El inicio nunca detiene un proceso que no sea `Pos.Server.exe`, un servidor POS que no responde en la dirección configurada ni una instancia ambigua. En esos casos informa el motivo y conserva el servicio existente.
3. Administración → Módulos ofrece una acción explícita para reiniciar el servidor local y cargar módulos pendientes. La acción advierte sobre la interrupción temporal de las cajas, espera a que el servidor vuelva a estar listo y muestra el estado resultante.
4. En una terminal cliente, la acción de reinicio remoto no se habilita; la interfaz indica que debe reiniciarse desde la computadora servidor.
5. Después del reinicio, las filas `RestartPending` se activan o muestran un diagnóstico de activación; el POS vuelve a consultar las pantallas y el menú refleja los módulos activos.
6. `Actualizar POS.cmd` conserva el orden respaldo → detener servidor → migrar → reemplazar archivos → abrir la versión nueva.
7. Administración → Módulos permite reactivar un módulo desactivado sin volver a subir el ZIP. El servidor marca el paquete instalado como pendiente, no repite respaldo ni migraciones y vuelve a verificar la firma y compatibilidad al reiniciar.
8. La interfaz identifica claramente que la reactivación requiere reiniciar el servidor; en una terminal cliente indica que se debe hacer desde la computadora servidor. Tras el reinicio, el módulo aparece activo o conserva un diagnóstico de error y el POS base sigue disponible.
9. La reactivación cambia el registro a `RestartPending` y guarda su auditoría en una sola transacción. Si falla, la interfaz vuelve a consultar el estado y muestra un código de diagnóstico del servidor en lugar de conservar una fila potencialmente obsoleta.

## Verificación

- Pruebas de política para detectar ruta de ejecutable antigua o binario actualizado durante la ejecución.
- Pruebas de contrato de la pantalla para confirmar la acción de reinicio y su límite en terminal cliente.
- `dotnet test` para las pruebas de escritorio y flujo de actualización.
- Prueba de contrato HTTP para reactivar un módulo con sesión autenticada y de interfaz para la acción disponible al seleccionar un módulo desactivado.
- Compilación de `Pos.Desktop` y `Pos.Server`.
