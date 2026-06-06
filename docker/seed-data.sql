-- =====================================================
-- SARIFF Seed Data with PROPER PASSWORD HASHES
-- =====================================================
-- Password for ALL test accounts: Test@123
-- BCrypt hash generated for "Test@123"
-- =====================================================

-- Clear existing test data (optional)
DELETE FROM "LoginHistories" WHERE "Id" IS NOT NULL;
DELETE FROM "OtpCodes" WHERE "Id" IS NOT NULL;
DELETE FROM "UserSessions" WHERE "Id" IS NOT NULL;
DELETE FROM "Transactions" WHERE "Id" IS NOT NULL;
DELETE FROM "ExchangeRates" WHERE "Id" IS NOT NULL;
DELETE FROM "ExpenseCategories" WHERE "Id" IS NOT NULL;
DELETE FROM "MpesaAgents" WHERE "Id" IS NOT NULL;
DELETE FROM "BankAccounts" WHERE "Id" IS NOT NULL;
DELETE FROM "CashAccounts" WHERE "Id" IS NOT NULL;
DELETE FROM "Users" WHERE "Id" IS NOT NULL;
DELETE FROM "Companies" WHERE "Id" IS NOT NULL;

-- =====================================================
-- 1. SUPER ADMIN (Role = 0)
-- Login: Code=SA-2026-001, Phone=+254700000000, Password=Test@123
-- =====================================================
INSERT INTO "Users" (
    "Id", "Code", "CompanyId", "FullName", "WhatsAppNumber", "Email", "IdPassport",
    "Role", "ClientType", "PasswordHash", "IsActive",
    "BalanceKES", "BalanceUSD", "OpeningBalanceKES", "OpeningBalanceUSD",
    "FailedLoginAttempts", "IsDeleted", "CreatedAt", "UpdatedAt"
)
VALUES (
           'a0000000-0000-0000-0000-000000000001',
           'SA-2026-001',
           NULL,
           'Super Admin',
           '+254700000000',
           'admin@sariff.com',
           NULL,
           0,
           NULL,
           '$2y$10$qwK5BIe2BWvWABMFnRGjweNZ6lWRc6T0egOwFtEcVhHJX8S5D7iei',
           true,
           0, 0, 0, 0, 0, false, NOW(), NOW()
       );

-- =====================================================
-- 2. COMPANIES (Office Users) - Role = 1
-- Company 1: Code=CO-2026-001, Phone=+254711111111, Password=Test@123
-- Company 2: Code=CO-2026-002, Phone=+254722222222, Password=Test@123
-- =====================================================
INSERT INTO "Companies" (
    "Id", "Code", "Name", "OwnerName", "WhatsAppNumber", "Email",
    "PasswordHash", "LogoUrl", "TaxId", "Website", "Address",
    "IsActive", "FailedLoginAttempts", "IsDeleted", "CreatedAt", "UpdatedAt"
)
VALUES
    (
        'b0000000-0000-0000-0000-000000000001',
        'CO-2026-001',
        'Alpha Forex Bureau',
        'John Kamau',
        '+254711111111',
        'john@alphaforex.co.ke',
        '$2y$10$qwK5BIe2BWvWABMFnRGjweNZ6lWRc6T0egOwFtEcVhHJX8S5D7iei',
        NULL, 'KRA123456', 'https://alphaforex.co.ke', 'Nairobi CBD, Kenya',
        true, 0, false, NOW(), NOW()
    ),
    (
        'b0000000-0000-0000-0000-000000000002',
        'CO-2026-002',
        'Beta Money Exchange',
        'Jane Wanjiku',
        '+254722222222',
        'jane@betamoney.co.ke',
        '$2y$10$qwK5BIe2BWvWABMFnRGjweNZ6lWRc6T0egOwFtEcVhHJX8S5D7iei',
        NULL, 'KRA789012', 'https://betamoney.co.ke', 'Mombasa Road, Kenya',
        true, 0, false, NOW(), NOW()
    );

-- =====================================================
-- 3. CLIENTS (Role = 2)
-- Client 1: Code=CL-2026-001, Phone=+254733333333, Password=Test@123 (NO OTP)
-- Client 2: Code=CL-2026-002, Phone=+254744444444, Password=Test@123 (NO OTP)
-- =====================================================
INSERT INTO "Users" (
    "Id", "Code", "CompanyId", "FullName", "WhatsAppNumber", "Email", "IdPassport",
    "Role", "ClientType", "PasswordHash", "IsActive",
    "BalanceKES", "BalanceUSD", "OpeningBalanceKES", "OpeningBalanceUSD",
    "FailedLoginAttempts", "IsDeleted", "CreatedAt", "UpdatedAt"
)
VALUES
    (
        'd0000000-0000-0000-0000-000000000001',
        'CL-2026-001',
        'b0000000-0000-0000-0000-000000000001',
        'Michael Ochieng',
        '+254733333333',
        'michael@email.com',
        '12345678',
        2, 0,
        '$2y$10$qwK5BIe2BWvWABMFnRGjweNZ6lWRc6T0egOwFtEcVhHJX8S5D7iei',
        true,
        50000.00, 500.00, 0, 0, 0, false, NOW(), NOW()
    ),
    (
        'd0000000-0000-0000-0000-000000000002',
        'CL-2026-002',
        'b0000000-0000-0000-0000-000000000001',
        'Grace Muthoni',
        '+254744444444',
        'grace@email.com',
        '87654321',
        2, 0,
        '$2y$10$qwK5BIe2BWvWABMFnRGjweNZ6lWRc6T0egOwFtEcVhHJX8S5D7iei',
        true,
        -25000.00, 0, 0, 0, 0, false, NOW(), NOW()
    );

-- =====================================================
-- 4. CASH ACCOUNTS
-- =====================================================
INSERT INTO "CashAccounts" ("Id", "CompanyId", "Currency", "Balance", "OpeningBalance", "IsDeleted", "CreatedAt", "UpdatedAt")
VALUES
    ('c1000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 0, 500000.00, 500000.00, false, NOW(), NOW()),
    ('c1000000-0000-0000-0000-000000000002', 'b0000000-0000-0000-0000-000000000001', 1, 5000.00, 5000.00, false, NOW(), NOW()),
    ('c1000000-0000-0000-0000-000000000003', 'b0000000-0000-0000-0000-000000000002', 0, 750000.00, 750000.00, false, NOW(), NOW()),
    ('c1000000-0000-0000-0000-000000000004', 'b0000000-0000-0000-0000-000000000002', 1, 8000.00, 8000.00, false, NOW(), NOW());

-- =====================================================
-- 5. BANK ACCOUNTS
-- =====================================================
INSERT INTO "BankAccounts" ("Id", "Code", "CompanyId", "BankName", "AccountNumber", "AccountName", "BranchCode", "Currency", "Balance", "OpeningBalance", "IsActive", "IsDeleted", "CreatedAt", "UpdatedAt")
VALUES
    ('c2000000-0000-0000-0000-000000000001', 'BA-2026-001', 'b0000000-0000-0000-0000-000000000001', 'Kenya Commercial Bank', '1234567890', 'Alpha Forex KES', '001', 0, 1500000.00, 1500000.00, true, false, NOW(), NOW()),
    ('c2000000-0000-0000-0000-000000000002', 'BA-2026-002', 'b0000000-0000-0000-0000-000000000001', 'Equity Bank', '0987654321', 'Alpha Forex USD', '002', 1, 25000.00, 25000.00, true, false, NOW(), NOW());

-- =====================================================
-- 6. M-PESA AGENTS
-- =====================================================
INSERT INTO "MpesaAgents" ("Id", "Code", "CompanyId", "AgentName", "PhoneNumber", "AgentNumber", "StoreNumber", "AgentType", "Balance", "OpeningBalance", "IsActive", "IsDeleted", "CreatedAt", "UpdatedAt")
VALUES
    ('c3000000-0000-0000-0000-000000000001', 'MP-2026-001', 'b0000000-0000-0000-0000-000000000001', 'Alpha Main Till', '+254711111111', '123456', '456789', 0, 150000.00, 150000.00, true, false, NOW(), NOW());

-- =====================================================
-- 7. EXCHANGE RATES
-- =====================================================
INSERT INTO "ExchangeRates" ("Id", "CompanyId", "BuyRate", "SellRate", "EffectiveFrom", "EffectiveTo", "IsActive", "CreatedByUserId", "IsDeleted", "CreatedAt", "UpdatedAt")
VALUES
    ('f0000000-0000-0000-0000-000000000001', 'b0000000-0000-0000-0000-000000000001', 128.50, 129.50, NOW(), NULL, true, 'a0000000-0000-0000-0000-000000000001', false, NOW(), NOW());

-- =====================================================
-- VERIFICATION
-- =====================================================
SELECT '===========================================' AS info;
SELECT 'SEED DATA LOADED SUCCESSFULLY!' AS status;
SELECT '===========================================' AS info;
SELECT '' AS blank;
SELECT 'TEST CREDENTIALS (Password: Test@123 for all):' AS info;
SELECT '' AS blank;
SELECT 'SUPER ADMIN (OTP Required):' AS role;
SELECT '  Code: SA-2026-001' AS credential;
SELECT '  Phone: +254700000000' AS credential;
SELECT '' AS blank;
SELECT 'OFFICE USER (OTP Required):' AS role;
SELECT '  Code: CO-2026-001' AS credential;
SELECT '  Phone: +254711111111' AS credential;
SELECT '' AS blank;
SELECT 'CLIENT (NO OTP):' AS role;
SELECT '  Code: CL-2026-001' AS credential;
SELECT '  Phone: +254733333333' AS credential;
SELECT '===========================================' AS info;
