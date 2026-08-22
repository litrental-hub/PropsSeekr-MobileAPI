#!/bin/bash
# Database migration script for PropSeekr unlock refactor

# Database credentials
DB_HOST="propseekr-db.cveo6kcqsisw.ap-south-1.rds.amazonaws.com"
DB_PORT="5432"
DB_USER="postgres"
DB_PASSWORD="08848bbeba4892b40fd6f720dcee07de3936e6e3"
DB_NAME="PropSeekr"

# Connection string
CONN_STRING="Server=$DB_HOST;Port=$DB_PORT;Database=$DB_NAME;User Id=$DB_USER;Password=$DB_PASSWORD;"

echo "Applying migrations to PropSeekr database..."
echo "Host: $DB_HOST"
echo "Database: $DB_NAME"
echo ""

cd "c:\Users\Aman Jain\source\repos\PropsSeekr-MobileAPI"

# Run the migration
dotnet ef database update --connection "$CONN_STRING"

echo ""
echo "Migration complete!"
echo ""
echo "Verify tables created:"
echo "- Matches"
echo "- MatchConfirmations"
echo "- Reveals"
echo "- CreditWallets"
echo "- CreditTransactions"
echo "- CreditPacks"
echo "- Payments"
