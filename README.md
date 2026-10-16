# Balanzapro — POS de escritorio

Sistema de punto de venta para Windows con cliente WPF, servicio ASP.NET Core y PostgreSQL. El servidor es la autoridad para ventas, existencias, permisos y auditoría; el cliente de escritorio se comunica con él mediante HTTP y no se conecta directamente a PostgreSQL.

## Estado del proyecto

- POS operativo con ventas, pagos mixtos, caja, clientes y crédito, inventario, precios y tasa, reportes, auditoría, operadores y permisos.
- Arquitectura de módulos firmados para agregar funciones de servidor, pantallas WPF, menús y extensiones declaradas por el anfitrión.
- **Almacenes y traslados 1.3.1** es el módulo piloto funcional. Incluye existencias por almacén, asignación de terminal, traslados con despacho/recepción, historial y pantalla propia. El ZIP firmado listo para instalar está en [`modulos/warehouses/1.3.1`](modulos/warehouses/1.3.1/README.md).
- La búsqueda F2 muestra la existencia asignada a la terminal; el cambio requiere actualizar el servidor POS principal. Ver [Hito 17](docs/specs/hito-17-f2-stock-por-terminal.md).
- El comprobante actual es de prueba y **no es una factura fiscal certificada**.

## Organización del repositorio

```text
src/
  Pos.Server/                     Servicio HTTP y lógica de anfitrión
  Pos.Desktop/                    Aplicación WPF del POS
  Pos.Infrastructure/             PostgreSQL, identidad y servicios
  Modules/                         Contratos, ejemplos y código fuente de módulos
modulos/                           Paquetes firmados listos para distribuir/instalar
docs/
  adr/                             Decisiones de arquitectura
  specs/                           Hitos, contratos y criterios de aceptación
  manuales/                        Guías de instalación, prueba y desarrollo
  security/                        Firma y custodia de claves
scripts/                           Compilación, publicación, instalación y pruebas
tests/                             Pruebas de dominio, aplicación, módulos e integración
artifacts/                         Compilaciones y paquetes locales (no se suben a Git)
```

El código fuente de Almacenes se mantiene en `src/Modules/Pos.Modules.Warehouses` y `src/Modules/Pos.Modules.Warehouses.Desktop`, porque esos proyectos forman parte de `Pos.sln`. La carpeta `modulos/` es el catálogo de entregas firmadas: conserva cada versión distribuible sin duplicar su código fuente.

## Requisitos de desarrollo

- Windows x64 y .NET 10 SDK.
- PostgreSQL para desarrollo e integración.
- Git.

Docker es opcional. Nunca configures las pruebas de integración contra la base activa de un comercio. Las pruebas que requieren PostgreSQL deben crear una base temporal desechable.

## Compilar y probar

Desde la raíz del repositorio:

```powershell
dotnet restore .\Pos.sln
dotnet build .\Pos.sln --no-restore
dotnet test .\Pos.sln --no-build
```

Las pruebas de integración PostgreSQL pueden requerir variables descritas por cada fixture. Usa siempre una instancia y credenciales de prueba con permiso para crear y eliminar bases temporales; nunca reutilices la conexión de producción.

## Ejecutar el POS

Para una instalación local, sigue [las instrucciones de instalación](docs/manuales/INSTRUCCIONES-INSTALACION-POS.md). El flujo de desarrollo requiere configurar la conexión PostgreSQL en el almacén seguro de .NET User Secrets o en la configuración segura local; no agregues contraseñas a `appsettings.json`, scripts ni Git.

Para preparar una distribución nueva de escritorio/servidor:

```powershell
.\scripts\Publish-LocalPos.ps1
```

El paquete se genera bajo `artifacts/`. `Actualizar POS.cmd` debe ejecutarse en la PC servidor con todas las ventanas del POS cerradas; verifica la conexión, crea el respaldo, prepara el esquema y reemplaza la instalación. No copies archivos sobre un servidor activo.

#### Descargar un paquete desde GitHub

En **Actions → Empaquetar POS para Windows → Run workflow**, selecciona la rama que quieres probar y ejecuta el flujo. Al terminar, descarga el Artifact `BALANZAPRO-POS-win-x64-...` y extrae su contenido: `Actualizar POS.cmd` queda en la carpeta principal junto con `desktop`, `server` y `scripts`.

Para publicar una versión estable, crea y sube un tag `pos-v...` después de revisar el cambio y sus resultados de CI. Por ejemplo:

```powershell
git tag pos-v1.0.0
git push origin pos-v1.0.0
```

El mismo flujo compila el paquete y adjunta `local-pos-win-x64.zip` y su suma SHA-256 a un GitHub Release. Descarga ese ZIP desde **Releases**, extráelo y ejecuta `Actualizar POS.cmd` en el servidor con el POS cerrado.

## Módulos

### Probar Almacenes y traslados

El paquete funcional actual es [`warehouses-1.3.1.zip`](modulos/warehouses/1.3.1/warehouses-1.3.1.zip). En la PC servidor, abre **Administración → Módulos**, elige el ZIP y pulsa **Validar paquete**. Revisa el plan y luego pulsa **Instalar módulo**. Después reinicia el servidor desde el botón de módulos y verifica que su estado cambie a `Active`.

La activación necesita un anfitrión POS compatible, respaldo PostgreSQL válido y reinicio del servicio. La terminal presenta la función cuando el módulo está activo y el usuario tiene permisos. Consulta [la guía del módulo](modulos/warehouses/1.3.1/README.md) y el [manual para crear módulos](docs/manuales/crear-modulos-pos.md).

### Crear, firmar y actualizar

1. Define un hito y criterios de aceptación antes de agregar comportamiento.
2. Crea el código bajo `src/Modules/Pos.Modules.<Nombre>` y, si aplica, `src/Modules/Pos.Modules.<Nombre>.Desktop`.
3. Añade pruebas, manifiesto y migraciones inmutables.
4. Construye el paquete firmado con el publicador oficial. La clave privada nunca se guarda en el repositorio; se puede distribuir la clave pública.
5. Verifica el ZIP firmado, documenta sus requisitos y agrega el release bajo `modulos/<id>/<versión>/`.
6. Actualiza la guía de módulos y el índice del README en el mismo cambio.

Una actualización de módulo conserva el mismo ID y usa una versión semántica mayor. Incluye los archivos de la versión completa y agrega una migración nueva si cambia el esquema; nunca alteres migraciones publicadas. El servidor realiza respaldo, migraciones y activación coordinada.

## Documentación principal

- [Arquitectura](docs/adr/ADR-001-arquitectura.md)
- [Módulos: arquitectura, instalación y actualización](docs/manuales/MODULOS-POS.md)
- [Guía paso a paso para programadores de módulos](docs/manuales/crear-modulos-pos.md)
- [Almacenes y traslados: entrega 1.3.1](modulos/warehouses/1.3.1/README.md)
- [Contrato API v1](docs/specs/contrato-api-v1.md)
- [Matriz de permisos](docs/specs/matriz-permisos.md)
- [Criterios de F2 por almacén de terminal](docs/specs/hito-17-f2-stock-por-terminal.md)
- [Reglas de contribución para agentes y programadores](AGENTS.md)

## Seguridad y archivos locales

No subas tokens, contraseñas, cadenas de conexión, certificados privados, claves de firma privadas, respaldos, datos de comercios ni resultados de compilación. `.gitignore` excluye los artefactos locales y secretos conocidos; antes de cada commit revisa `git status` y el contenido staged.
