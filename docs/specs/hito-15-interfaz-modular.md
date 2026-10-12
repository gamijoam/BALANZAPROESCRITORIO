# Hito 15: interfaz de escritorio modular

## Criterios de aceptación

1. Un paquete firmado puede declarar un punto de entrada de servidor y uno de escritorio, cada uno validado contra una versión de API distinta.
2. Un módulo de escritorio oficial puede registrar comandos de menú y extensiones declaradas por el anfitrión; no puede reemplazar arbitrariamente las pantallas de Venta o Cobro.
3. El anfitrión presenta contribuciones solo para módulos activos y solo cuando el usuario tiene el permiso requerido. La autorización del servidor sigue siendo obligatoria para cada operación.
4. Una terminal autenticada puede consultar qué módulos activos ofrecen interfaz y descargar el ZIP firmado desde el servidor. La descarga debe corresponder al mismo ID y versión registrados como activos.
5. La terminal valida firma, hash, punto de entrada y compatibilidad de API antes de cargar el ensamblado; los fallos de un módulo no impiden abrir el POS base.
6. Almacenes es el piloto: su botón, ventana y selector de almacén los aporta el ensamblado de escritorio del módulo. El filtro de inventario solo se registra si Almacenes está activo y autorizado.
7. Una instalación base sin el módulo no muestra opciones de Almacenes y conserva la venta y el inventario global.
8. Las pruebas de integración confirman descarga, compatibilidad y permisos; las pruebas PostgreSQL existentes se ejecutan con base temporal y no con datos locales del comercio.

## Restricciones

- La WPF consume contratos HTTP; los módulos de escritorio no abren conexiones PostgreSQL.
- Los paquetes ejecutables se consideran código confiable firmado, no código aislado.
- Las terminales y el servidor cargan nuevas versiones en un reinicio coordinado fuera de operaciones críticas.
- Todo punto de extensión nuevo requiere una versión compatible del anfitrión; módulos posteriores pueden reutilizarlo sin reemplazar el ejecutable.
