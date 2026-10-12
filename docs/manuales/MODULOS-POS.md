# Módulos del POS: guía y estado

Esta página es el punto de entrada para entender, crear, instalar, actualizar y mantener módulos del POS. Las instrucciones detalladas viven en las referencias enlazadas al final.

## Qué aporta un módulo

Un módulo firmado puede agregar endpoints al servicio, migraciones propias, pantallas WPF y comandos de menú. El anfitrión POS conserva el inicio de sesión, permisos, navegación, cliente HTTP y puntos de extensión autorizados. La pantalla del módulo solicita datos mediante HTTP; no se conecta directamente a PostgreSQL.

El módulo de **Almacenes y traslados** es el piloto. Puede aportar una pantalla y botón propios y extender Inventario con el filtro de almacén. Venta y Cobro siguen siendo pantallas del anfitrión y no admiten reemplazos arbitrarios. Un punto de extensión nuevo se agrega al anfitrión una vez; las versiones futuras de módulos compatibles pueden usarlo sin reemplazar el ejecutable base.

Los módulos ejecutables firmados se consideran código confiable dentro del proceso, no código aislado. Solo se deben instalar paquetes de publicadores confiables.

## Estado y conceptos

- **ID interno:** identificador estable que viene del `module.manifest.json`, por ejemplo `warehouses`. No se inventa a partir del nombre del ZIP; la pantalla lee el ID del paquete.
- **Nombre visible:** nombre pensado para operadores, por ejemplo “Almacenes y traslados”.
- **Versión del módulo:** versión semántica del manifiesto, independiente de la versión del POS.
- **Host API:** intervalo de contratos del servicio que acepta el módulo.
- **Desktop API:** versión del contrato WPF que acepta la pantalla del módulo.
- **Terminales:** intervalo compatible de versiones del cliente POS. La activación se bloquea si una terminal conectada reporta una versión incompatible.
- **`RestartPending`:** paquete instalado o módulo reactivado que requiere reiniciar el servidor para verificarse y cargarse. Cerrar una ventana o reabrir únicamente la interfaz no sustituye el reinicio del servicio.
- **`Active`:** servidor cargó correctamente el módulo. El menú solo se muestra en terminales compatibles y con permiso.
- **`Disabled`:** módulo desactivado administrativamente. Se rechazan sus operaciones y se ocultan sus opciones; el ZIP y los datos se conservan.
- **Error de activación:** el POS base permanece disponible. El diagnóstico se muestra en Administración → Módulos y se registra en el log local del servidor.

## Flujo operativo

### Crear y publicar

1. Definir el alcance como hito, con criterios de aceptación, propietario de datos, permisos, dependencias, migraciones y estrategia de recuperación.
2. Crear el módulo bajo `src/Modules/Pos.Modules.<Nombre>` y, si tiene interfaz, su proyecto `Pos.Modules.<Nombre>.Desktop`.
3. Implementar los contratos publicados por el anfitrión y pruebas de dominio, permisos, manifiesto, migraciones, idempotencia y operaciones simultáneas.
4. Agregar una migración nueva por cambio de esquema. No modificar migraciones ya publicadas.
5. Compilar y recopilar en staging solo los archivos explícitos del módulo.
6. Firmar el ZIP con `Pos.Modules.Publisher`. La clave privada queda en el almacén seguro del publicador; el servidor solo recibe la clave pública.
7. Verificar localmente el ZIP firmado y entregar el paquete junto con sus notas de versión y migración.

### Instalar y activar

1. En la PC servidor, abre Administración → Módulos y selecciona el ZIP. El sistema obtiene del manifiesto el ID, nombre y versión; no escribas el nombre del archivo como ID.
2. Pulsa **Validar paquete** y revisa el plan, dependencias, compatibilidad de anfitrión y terminales, permisos y migraciones.
3. Pulsa **Instalar módulo**. El servicio valida otra vez, espera que el punto de mantenimiento esté libre, crea y comprueba el respaldo, aplica las migraciones y guarda el paquete firmado. Si algo falla, no activa el módulo.
4. El estado queda `RestartPending`. En la computadora servidor usa **Reiniciar servidor y cargar módulos**. La acción espera que el servicio vuelva a estar listo y consulta el estado nuevo.
5. Confirma `Active`, revisa el diagnóstico vacío y valida el menú y la pantalla en las terminales compatibles. Las terminales cliente no reinician el servicio remoto; el reinicio se hace en la PC servidor.

### Actualizar una versión

Una actualización es un ZIP nuevo con el mismo ID y una versión superior. Para `warehouses` 1.0.0 → 1.1.0:

1. El nuevo ZIP incluye los archivos necesarios de la versión 1.1.0; no se instala encima copiando solo los archivos modificados.
2. Conserva las migraciones anteriores en el repositorio del módulo y agrega una migración `0002` si hay cambios de esquema. El servicio guarda el ZIP de la versión instalada y ejecuta solo las migraciones pendientes según sus identificadores y checksums.
3. Valida compatibilidad, respaldo y plan. Instala desde Administración → Módulos y reinicia el servidor.
4. Verifica que se activó la versión nueva, revisa su diagnóstico y prueba las rutas críticas. El respaldo permite recuperar datos si la migración necesita recuperación.
5. Actualiza los POS de las terminales cuando la versión de escritorio o su `Desktop API` lo requieran. No actualices el anfitrión si el módulo usa contratos que ya están publicados y las versiones instaladas son compatibles.

### Desactivar y reactivar

- **Desactivar:** selecciona el módulo instalado y pulsa **Desactivar módulo seleccionado**. Las rutas y pantallas dejan de estar disponibles; datos y ZIP permanecen.
- **Reactivar:** selecciona la fila `Disabled` y pulsa **Reactivar módulo seleccionado**. No subas el ZIP de nuevo y no se repiten respaldo ni migraciones. El estado vuelve a `RestartPending`; reinicia el servidor y verifica que termine en `Active`.
- Si persiste un estado incorrecto, copia el diagnóstico visible y busca el código en el log indicado por la interfaz antes de repetir la instalación.

## Qué requiere actualizar el anfitrión

El módulo puede utilizar los contratos y puntos de extensión disponibles en la versión instalada. Se necesita una actualización única del POS anfitrión cuando el módulo requiere un contrato, permiso, endpoint de integración o punto de extensión que esa versión no publicó. Después, otros módulos pueden usar el contrato existente siempre que declaren compatibilidad. Un paquete no puede inyectar libremente controles en cualquier pantalla.

## Referencias

- [Guía paso a paso para crear e integrar módulos](crear-modulos-pos.md)
- [Formato de paquete firmado v1](../specs/paquete-modulo-v1.md)
- [ADR-002: módulos y paquetes](../adr/ADR-002-modulos-y-paquetes.md)
- [Hito 13: instalación de módulos](../specs/hito-13-instalacion-de-modulos.md)
- [Hito 14: módulo de almacenes](../specs/hito-14-modulo-almacenes.md)
- [Hito 15: interfaz de escritorio modular](../specs/hito-15-interfaz-modular.md)
- [Hito 16: reinicio y ciclo de activación](../specs/hito-16-reinicio-servidor-y-modulos.md)
- [Publicar paquetes firmados](../security/publicar-modulos.md)
- [Custodia y rotación de claves](../security/claves-firmantes-de-modulos.md)
- [Prueba manual de Almacenes](PROBAR-MODULO-ALMACENES.md)
- [Actualización de Almacenes 1.1.0](INSTRUCCIONES-ACTUALIZACION-ALMACENES-1.1.0.md)

## Mantenimiento de esta documentación

Al cambiar contratos, formato de manifiesto, estados, comandos de publicación, migraciones, reinicio o instalación, actualiza la referencia específica y este índice en el mismo cambio. Mantén los ejemplos sin credenciales, tokens, certificados privados ni datos de comercios. Describe como pendiente cualquier validación que no tenga evidencia en una base temporal o terminal de prueba.
