# Plantilla de módulo POS

Duplica esta carpeta para iniciar una capacidad nueva. Cambia `example-module` por un id estable en minúsculas y guiones y ajusta la versión, compatibilidad, permisos, dependencias, licencia y migraciones declaradas.

## Estructura prevista para un módulo real

```text
<ModuleName>/
├── Pos.Modules.<ModuleName>.Contracts/  # Tipos públicos consumidos por otros módulos
├── Pos.Modules.<ModuleName>/
│   ├── Domain/
│   ├── Application/
│   ├── Infrastructure/
│   ├── Endpoints/
│   ├── Desktop/                         # Solo presentación integrada en el shell WPF
│   ├── Migrations/                      # Ejecutadas exclusivamente por el servidor
│   ├── <ModuleName>Module.cs            # Registro explícito en el anfitrión
│   └── module.manifest.json
└── README.md
```

Los contratos HTTP existentes siguen siendo la frontera de WPF: el escritorio no conecta a PostgreSQL ni ejecuta migraciones. Un módulo no referencia la infraestructura interna de otro módulo; usa contratos públicos o eventos del anfitrión. Las migraciones declaran ids únicos propiedad del módulo y nunca incluyen credenciales.

Las migraciones nuevas usan el DSL JSON firmado en `migrations/<module-id>-<migration-suffix>.json`; consulta `migrations/example-module-0001.json`. El DSL solo permite crear tablas/índices y agregar columnas nullable con tipos aprobados. No escribas SQL libre.

## Estado actual

Esta plantilla organiza módulos nuevos, pero no trae código ejecutable. Hay un ejemplo separado en `src/Modules/Pos.Modules.Example` que implementa `IPosServerModule`, declara un endpoint y una migración aditiva. El servidor puede instalar paquetes firmados, guardar respaldo, aplicar el DSL, registrar `RestartPending` y cargar el punto de entrada en memoria al reiniciar. Todas las rutas quedan bajo `/api/v1/modules/<id>` y requieren sesión autenticada con permiso `ManageModules`.

La firma se vuelve a verificar en cada arranque; la carga falla cerrada si el paquete, servidor o alguna terminal no son compatibles. Un módulo que falla no detiene el POS base. Los módulos oficiales corren dentro del proceso del servidor: solo publica código que controles y firmes. La prueba automatizada del cargador usa firma efímera; la instalación, migración, respaldo y activación completa con PostgreSQL requiere una base temporal y un usuario de prueba con permiso para crearla. No se ejecuta contra la base local del comercio.

Al crear una función nueva, agrega pruebas de aceptación primero y registra el proyecto de forma explícita en la solución. No muevas los proyectos existentes hasta que cada extracción tenga sus propias pruebas de arquitectura e integración.
