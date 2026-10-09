# Instrucciones del proyecto

- Seguir los hitos aprobados en `Prompt_Maestro_POS_Escritorio_TDD.md`.
- No implementar funciones de negocio sin aprobación del hito correspondiente.
- Para cada historia, redactar criterios de aceptación y escribir una prueba que falle por el comportamiento esperado antes de implementar.
- No afirmar ejecuciones de pruebas, integración, compatibilidad fiscal o de periféricos sin evidencia real.
- La aplicación WPF consume contratos HTTP; nunca accede directamente a PostgreSQL.
- El servicio es la autoridad para permisos, integridad de ventas e inventario, idempotencia y auditoría.
- Mantener importes monetarios en `decimal`/`numeric`; no usar `float`/`double` para dinero.
- No almacenar secretos ni datos locales de comercios en Git.
