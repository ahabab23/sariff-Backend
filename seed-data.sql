-- SARIFF Seed Data
-- Run: docker exec -i sariff-postgres psql -U postgres -d sariff_db < seed-data.sql
-- Default password for all test accounts: Test@123

-- =====================================================
-- 1. SUPER ADMIN (Role = 0)
-- =====================================================
INSERT INTO "Users" (
    "Id", "Code", "CompanyId", "FullName", "WhatsAppNumber", "Email", "IdPassport",
    "Role", "ClientType", "PasswordHash", "IsActive",
    "BalanceKES", "BalanceUSD", "OpeningBalanceKES", "OpeningBalanceUSD",
    "FailedLoginAttempts", "IsDeleted", "CreatedAt", "UpdatedAt"
)
VALUES (
           'a0000000-0000-0000-0000-000000000001', 'SA-2026-001', NULL, 'Super Admin', '+254700000000',
           'admin@sariff.com', NULL, 0, NULL, NULL, true,
           0, 0, 0, 0, 0, false, NOW(), NOW()
       )
    ON CONFLICT ("Id") DO NOTHING;

-- =====================================================
-- 2. COMPANIES (Office Users)
-- Password: Test@123 (BCrypt hash)
-- =====================================================
INSERT INTO "Companies" (
    "Id", "Code", "Name", "OwnerName", "WhatsAppNumber", "Email",
    "PasswordHash", "LogoUrl", "TaxId", "Website", "Address",
    "IsActive", "FailedLoginAttempts", "IsDeleted", "CreatedAt", "UpdatedAt"
)
VALUES
    (
        'b0000000-0000-0000-0000-000000000001', 'CO-2026-001',
        'Alpha Forex Bureau', 'John Kamau', '+254711111111', 'john@alphaforex.co.ke',
        '$2a$11$K2CtDP9nVlLkYFKXvqhL5.Tl.rhCBaLKJ5Mc5n6vH.hOJGOTN0Pum',
        NULL, 'KRA123456', 'https://alphaforex.co.ke', 'Nairobi CBD, Kenya',
        true, 0, false, NOW(), NOW()
    ),
    (
        'b0000000-0000-0000-0000-000000000002', 'CO-2026-002',
        'Beta Money Exchange', 'Jane Wanjiku', '+254722222222', 'jane@betamoney.co.ke',
        '$2a$11$K2CtDP9nVlLkYFKXvqhL5.Tl.rhCBaLKJ5Mc5n6vH.hOJGOTN0Pum',
        NULL, 'KRA789012', 'https://betamoney.co.ke', 'Mombasa Road, Kenya',
        true, 0, false, NOW(), NOW()
    )
    ON CONFLICT ("Id") DO NOTHING;

-- =====================================================
-- 3. CASH ACCOUNTS (KES=0, USD=1)
-- =====================================================
INSERT INTO "CashAccounts" (
    "Id", "CompanyId", "Currency", "Balance", "OpeningBalance",
    "IsDeleted", "CreatedAt", "UpdatedAt"
)
VALUES
    ('c1000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 0, 500000.00, 500000.00, false, NOW(), NOW()),
    ('c1000000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', 1, 5000.00, 5000.00, false, NOW(), NOW()),
    ('c1000000-0000-0000-0000-000000000003', 'b0000000-0000-0000-0000-000000000002', 0, 750000.00, 750000.00, false, NOW(), NOW()),
    ('c1000000-0000-0000-0000-000000000004', 'b0000000-0000-0000-0000-000000000002', 1, 8000.00, 8000.00, false, NOW(), NOW())
    ON CONFLICT ("Id") DO NOTHING;

-- =====================================================
-- 4. BANK ACCOUNTS
-- =====================================================
INSERT INTO "BankAccounts" (
    "Id", "Code", "CompanyId", "BankName", "AccountNumber", "AccountName", "BranchCode",
    "Currency", "Balance", "OpeningBalance", "IsActive", "IsDeleted", "CreatedAt", "UpdatedAt"
)
VALUES
    ('c2000000-0000-0000-0000-000000000001', 'BA-2026-001', 'b0000000-0000-0000-0000-000000000001',
     'Kenya Commercial Bank', '1234567890', 'Alpha Forex KES', '001',
     0, 1500000.00, 1500000.00, true, false, NOW(), NOW()),
    ('c2000000-0000-0000-0000-000000000002', 'BA-2026-002', 'b0000000-0000-0000-0000-000000000001',
     'Equity Bank', '0987654321', 'Alpha Forex USD', '002',
     1, 25000.00, 25000.00, true, false, NOW(), NOW()),
    ('c2000000-0000-0000-0000-000000000003', 'BA-2026-003', 'b0000000-0000-0000-0000-000000000002',
     'Cooperative Bank', '5678901234', 'Beta Money KES', '010',
     0, 800000.00, 800000.00, true, false, NOW(), NOW()),
    ('c2000000-0000-0000-0000-000000000004', 'BA-2026-004', 'b0000000-0000-0000-0000-000000000002',
     'Standard Chartered', '4321098765', 'Beta Money USD', '015',
     1, 15000.00, 15000.00, true, false, NOW(), NOW())
    ON CONFLICT ("Id") DO NOTHING;

-- =====================================================
-- 5. M-PESA AGENTS
-- =====================================================
INSERT INTO "MpesaAgents" (
    "Id", "Code", "CompanyId", "AgentName", "PhoneNumber", "AgentNumber", "StoreNumber",
    "AgentType", "Balance", "OpeningBalance", "IsActive", "IsDeleted", "CreatedAt", "UpdatedAt"
)
VALUES
    ('c3000000-0000-0000-0000-000000000001', 'MP-2026-001', 'b0000000-0000-0000-0000-000000000001',
     'Alpha Main Till', '+254711111111', '123456', '456789',
     0, 150000.00, 150000.00, true, false, NOW(), NOW()),
    ('c3000000-0000-0000-0000-000000000002', 'MP-2026-002', 'b0000000-0000-0000-0000-000000000001',
     'Alpha Paybill', '+254711111112', '888888', NULL,
     1, 250000.00, 250000.00, true, false, NOW(), NOW()),
    ('c3000000-0000-0000-0000-000000000003', 'MP-2026-003', 'b0000000-0000-0000-0000-000000000002',
     'Beta Till', '+254722222222', '654321', '987654',
     0, 200000.00, 200000.00, true, false, NOW(), NOW())
    ON CONFLICT ("Id") DO NOTHING;

-- =====================================================
-- 6. CLIENTS (Role=2, ClientType: Permanent=0, Temporary=1)
-- =====================================================
INSERT INTO "Users" (
    "Id", "Code", "CompanyId", "FullName", "WhatsAppNumber", "Email", "IdPassport",
    "Role", "ClientType", "PasswordHash", "IsActive",
    "BalanceKES", "BalanceUSD", "OpeningBalanceKES", "OpeningBalanceUSD",
    "FailedLoginAttempts", "IsDeleted", "CreatedAt", "UpdatedAt"
)
VALUES
    (
        'd0000000-0000-0000-0000-000000000001', 'CL-2026-001', 'b0000000-0000-0000-0000-000000000001',
        'Michael Ochieng', '+254733333333', 'michael@email.com', '12345678',
        2, 0, '$2a$11$K2CtDP9nVlLkYFKXvqhL5.Tl.rhCBaLKJ5Mc5n6vH.hOJGOTN0Pum', true,
        50000.00, 500.00, 0, 0, 0, false, NOW(), NOW()
    ),
    (
        'd0000000-0000-0000-0000-000000000002', 'CL-2026-002', 'b0000000-0000-0000-0000-000000000001',
        'Grace Muthoni', '+254744444444', 'grace@email.com', '87654321',
        2, 0, '$2a$11$K2CtDP9nVlLkYFKXvqhL5.Tl.rhCBaLKJ5Mc5n6vH.hOJGOTN0Pum', true,
        -25000.00, 0, 0, 0, 0, false, NOW(), NOW()
    ),
    (
        'd0000000-0000-0000-0000-000000000003', 'CL-2026-003', 'b0000000-0000-0000-0000-000000000001',
        'Peter Kimani', '+254755555555', NULL, NULL,
        2, 1, NULL, true,
        10000.00, 100.00, 0, 0, 0, false, NOW(), NOW()
    ),
    (
        'd0000000-0000-0000-0000-000000000004', 'CL-2026-004', 'b0000000-0000-0000-0000-000000000002',
        'Sarah Akinyi', '+254766666666', 'sarah@email.com', '11223344',
        2, 0, '$2a$11$K2CtDP9nVlLkYFKXvqhL5.Tl.rhCBaLKJ5Mc5n6vH.hOJGOTN0Pum', true,
        75000.00, 1000.00, 0, 0, 0, false, NOW(), NOW()
    )
    ON CONFLICT ("Id") DO NOTHING;

-- =====================================================
-- 7. EXPENSE CATEGORIES
-- =====================================================
INSERT INTO "ExpenseCategories" (
    "Id", "CompanyId", "Name", "Description", "IsActive", "IsDeleted", "CreatedAt", "UpdatedAt"
)
VALUES
    ('e1000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 'Rent', 'Office rent payments', true, false, NOW(), NOW()),
    ('e1000000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', 'Utilities', 'Electricity, water, internet', true, false, NOW(), NOW()),
    ('e1000000-0000-0000-0000-000000000003', 'b0000000-0000-0000-0000-000000000001', 'Salaries', 'Staff salaries', true, false, NOW(), NOW()),
    ('e1000000-0000-0000-0000-000000000004', 'b0000000-0000-0000-0000-000000000001', 'Transport', 'Transport and fuel', true, false, NOW(), NOW()),
    ('e1000000-0000-0000-0000-000000000005', 'b0000000-0000-0000-0000-000000000002', 'Rent', 'Office rent', true, false, NOW(), NOW()),
    ('e1000000-0000-0000-0000-000000000006', 'b0000000-0000-0000-0000-000000000002', 'Utilities', 'Bills', true, false, NOW(), NOW()),
    ('e1000000-0000-0000-0000-000000000007', 'b0000000-0000-0000-0000-000000000002', 'Salaries', 'Staff pay', true, false, NOW(), NOW()),
    ('e1000000-0000-0000-0000-000000000008', 'b0000000-0000-0000-0000-000000000002', 'Marketing', 'Advertising', true, false, NOW(), NOW())
    ON CONFLICT ("Id") DO NOTHING;

-- =====================================================
-- 8. EXCHANGE RATES
-- =====================================================
INSERT INTO "ExchangeRates" (
    "Id", "CompanyId", "BuyRate", "SellRate", "EffectiveFrom", "EffectiveTo",
    "IsActive", "CreatedByUserId", "IsDeleted", "CreatedAt", "UpdatedAt"
)
VALUES
    ('f0000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001',
     128.50, 129.50, NOW(), NULL, true, NULL, false, NOW(), NOW()),
    ('f0000000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000002',
     128.00, 130.00, NOW(), NULL, true, NULL, false, NOW(), NOW())
    ON CONFLICT ("Id") DO NOTHING;

-- =====================================================
-- VERIFICATION
-- =====================================================
SELECT 'Seed data inserted!' AS status;

SELECT 'Users' AS entity, COUNT(*) AS count FROM "Users"
UNION ALL SELECT 'Companies', COUNT(*) FROM "Companies"
          UNION ALL SELECT 'Cash Accounts', COUNT(*) FROM "CashAccounts"
          UNION ALL SELECT 'Bank Accounts', COUNT(*) FROM "BankAccounts"
          UNION ALL SELECT 'M-Pesa Agents', COUNT(*) FROM "MpesaAgents"
          UNION ALL SELECT 'Expense Categories', COUNT(*) FROM "ExpenseCategories"
          UNION ALL SELECT 'Exchange Rates', COUNT(*) FROM "ExchangeRates";