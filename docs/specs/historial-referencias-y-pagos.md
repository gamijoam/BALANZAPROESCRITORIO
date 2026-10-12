# Historial: referencias monetarias y pagos

## Criterios de aceptación

1. Al confirmar una venta se conserva la tasa USD/VES disponible en ese momento, sin depender de la tasa configurada después.
2. En el historial, las ventas en VES muestran su referencia en USD y las ventas en USD muestran su referencia en VES, calculadas con la tasa guardada.
3. Si una venta histórica no tiene tasa guardada ni datos de conversión suficientes para reconstruirla, se muestra «referencia no disponible»; nunca se usa la tasa actual como sustituto.
4. La lista del historial muestra los importes recibidos por moneda, incluyendo cobros mixtos en VES y USD. El comprobante seleccionado mantiene el desglose por método, moneda y monto.
5. Las consultas del historial no modifican ventas, pagos ni tasas guardadas.

## Prueba previa a la implementación

`SalesHistoryUxTests` verifica que la referencia sale de la tasa persistida, que el resumen presenta ambas monedas y que no inventa una referencia para ventas antiguas sin datos históricos.
