using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Hito11UsdCreditAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AppliedExchangeRate",
                table: "receivable_entries",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OriginalAmount",
                table: "receivable_entries",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalCurrency",
                table: "receivable_entries",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM customers WHERE "Currency" = 'VES' AND "CreditLimit" > 0)
                       AND NOT EXISTS (SELECT 1 FROM exchange_rates WHERE "EffectiveAtUtc" <= CURRENT_TIMESTAMP
                           AND (("FromCurrency" = 'USD' AND "ToCurrency" = 'VES') OR ("FromCurrency" = 'VES' AND "ToCurrency" = 'USD'))) THEN
                        RAISE EXCEPTION 'Cannot convert VES customer limits without an exchange rate';
                    END IF;
                    IF EXISTS (SELECT 1 FROM receivable_entries WHERE "Currency" = 'VES' AND NOT EXISTS
                        (SELECT 1 FROM exchange_rates r WHERE r."EffectiveAtUtc" <= receivable_entries."OccurredAtUtc"
                         AND ((r."FromCurrency" = 'USD' AND r."ToCurrency" = 'VES') OR (r."FromCurrency" = 'VES' AND r."ToCurrency" = 'USD')))) THEN
                        RAISE EXCEPTION 'Cannot convert a VES receivable entry without its historical exchange rate';
                    END IF;
                END $$;

                WITH conversions AS (
                    SELECT e."Id", e."Amount", e."Currency", fx."FromCurrency", fx."Rate"
                    FROM receivable_entries e
                    CROSS JOIN LATERAL (
                        SELECT r."FromCurrency", r."Rate" FROM exchange_rates r
                        WHERE r."EffectiveAtUtc" <= e."OccurredAtUtc"
                          AND ((r."FromCurrency" = 'USD' AND r."ToCurrency" = 'VES') OR (r."FromCurrency" = 'VES' AND r."ToCurrency" = 'USD'))
                        ORDER BY r."EffectiveAtUtc" DESC, CASE WHEN r."FromCurrency" = 'USD' THEN 0 ELSE 1 END
                        LIMIT 1
                    ) AS fx
                    WHERE e."Currency" = 'VES'
                )
                UPDATE receivable_entries AS e
                SET "OriginalAmount" = conversions."Amount", "OriginalCurrency" = conversions."Currency",
                    "AppliedExchangeRate" = CASE WHEN conversions."FromCurrency" = 'USD' THEN ROUND(1::numeric / conversions."Rate", 8) ELSE ROUND(conversions."Rate", 8) END,
                    "Amount" = ROUND(conversions."Amount" * CASE WHEN conversions."FromCurrency" = 'USD' THEN 1::numeric / conversions."Rate" ELSE conversions."Rate" END, 2),
                    "Currency" = 'USD'
                FROM conversions WHERE e."Id" = conversions."Id";

                UPDATE receivable_entries SET "OriginalAmount" = "Amount", "OriginalCurrency" = "Currency"
                WHERE "OriginalAmount" IS NULL;

                WITH current_fx AS (
                    SELECT CASE WHEN r."FromCurrency" = 'USD' THEN r."Rate" ELSE 1::numeric / r."Rate" END AS "UsdToVes"
                    FROM exchange_rates r
                    WHERE r."EffectiveAtUtc" <= CURRENT_TIMESTAMP
                      AND ((r."FromCurrency" = 'USD' AND r."ToCurrency" = 'VES') OR (r."FromCurrency" = 'VES' AND r."ToCurrency" = 'USD'))
                    ORDER BY r."EffectiveAtUtc" DESC, CASE WHEN r."FromCurrency" = 'USD' THEN 0 ELSE 1 END
                    LIMIT 1
                )
                UPDATE customers AS c
                SET "CreditLimit" = ROUND(c."CreditLimit" / current_fx."UsdToVes", 2), "Currency" = 'USD'
                FROM current_fx WHERE c."Currency" = 'VES' AND c."CreditLimit" > 0;

                UPDATE customers SET "Currency" = 'USD' WHERE "Currency" = 'VES';

                WITH sale_rates AS (
                    SELECT sl."SaleId", MIN(sl."AppliedExchangeRate") AS "VesPerUsd", MAX(sl."AppliedExchangeRate") AS "VesPerUsdMax"
                    FROM sale_lines sl
                    WHERE sl."OriginalCurrency" = 'USD' AND sl."AppliedExchangeRate" > 0
                    GROUP BY sl."SaleId"
                ), candidates AS (
                    SELECT s."Id" AS "SaleId", s."CustomerId", s."OperatorId", s."TerminalId", s."Currency",
                           SUM(p."Amount") AS "OriginalAmount",
                           CASE WHEN s."Currency" = 'VES' THEN sr."VesPerUsd" ELSE NULL END AS "VesPerUsd"
                    FROM sales s
                    JOIN customers c ON c."Id" = s."CustomerId"
                    JOIN sale_payments p ON p."SaleId" = s."Id"
                    LEFT JOIN sale_rates sr ON sr."SaleId" = s."Id"
                    WHERE s."Status" = 'Confirmed'
                      AND (LOWER(p."Method") LIKE 'cr%' OR LOWER(p."Method") = 'fiado')
                      AND p."Currency" = s."Currency"
                      AND s."Currency" IN ('USD', 'VES')
                      AND NOT EXISTS (SELECT 1 FROM receivable_entries existing WHERE existing."SaleId" = s."Id")
                    GROUP BY s."Id", s."CustomerId", s."OperatorId", s."TerminalId", s."Currency", sr."VesPerUsd", sr."VesPerUsdMax"
                    HAVING s."Currency" = 'USD' OR (sr."VesPerUsd" = sr."VesPerUsdMax" AND sr."VesPerUsd" > 0)
                ), inserted AS (
                    INSERT INTO receivable_entries
                        ("Id", "CustomerId", "SaleId", "EntryType", "Amount", "Currency", "OriginalAmount", "OriginalCurrency", "AppliedExchangeRate", "Reason", "ActorId", "TerminalId", "OccurredAtUtc")
                    SELECT c."SaleId", c."CustomerId", c."SaleId", 'Charge',
                           CASE WHEN c."Currency" = 'USD' THEN c."OriginalAmount" ELSE ROUND(c."OriginalAmount" / c."VesPerUsd", 2) END,
                           'USD', c."OriginalAmount", c."Currency",
                           CASE WHEN c."Currency" = 'USD' THEN NULL ELSE ROUND(1::numeric / c."VesPerUsd", 8) END,
                           'Crédito histórico recuperado con la tasa guardada en la venta', c."OperatorId", c."TerminalId", s."CreatedAtUtc"
                    FROM candidates c JOIN sales s ON s."Id" = c."SaleId"
                    RETURNING "Id", "CustomerId", "SaleId", "Amount", "OriginalAmount", "OriginalCurrency", "AppliedExchangeRate", "ActorId", "TerminalId", "OccurredAtUtc"
                )
                INSERT INTO audit_events ("Id", "ActorId", "TerminalId", "Action", "Payload", "OccurredAtUtc")
                SELECT md5(i."Id"::text || ':credit-recovery')::uuid, i."ActorId", i."TerminalId", 'receivable.credit_recovered',
                       jsonb_build_object('customerId', i."CustomerId", 'saleId', i."SaleId", 'amountUsd', i."Amount", 'originalAmount', i."OriginalAmount", 'originalCurrency', i."OriginalCurrency", 'appliedExchangeRate', i."AppliedExchangeRate"), i."OccurredAtUtc"
                FROM inserted i;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppliedExchangeRate",
                table: "receivable_entries");

            migrationBuilder.DropColumn(
                name: "OriginalAmount",
                table: "receivable_entries");

            migrationBuilder.DropColumn(
                name: "OriginalCurrency",
                table: "receivable_entries");
        }
    }
}
