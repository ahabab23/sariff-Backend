# SARIFF Backend - API Documentation

## Overview

SARIFF (SARiff) is a comprehensive Forex Bureau Management System built with .NET 8. The system provides a complete solution for managing forex bureau operations including client management, transactions, multi-currency accounting, reconciliation, and reporting.

## Architecture

### Project Structure

```
SARIFF-Backend/
├── src/
│   ├── SARIFF.API/          # Web API Layer
│   ├── SARIFF.Core/         # Domain Layer (Entities, DTOs, Interfaces)
│   └── SARIFF.Infrastructure/  # Infrastructure Layer (Services, Data)
├── docker/
├── SARIFF.sln
└── README.md
```

### Technology Stack

- **Framework**: ASP.NET Core 8.0
- **Database**: PostgreSQL (via Entity Framework Core)
- **Authentication**: JWT Bearer Tokens
- **Real-time**: SignalR
- **Logging**: Serilog
- **API Documentation**: Swagger/OpenAPI

## User Roles

| Role | Description |
|------|-------------|
| `SuperAdmin` | Platform administrator - manages all companies |
| `OfficeUser` | Company/Business subscriber - manages their bureau |
| `Client` | End customer - can view their own transactions |

## Entities

### Core Entities

| Entity | Description |
|--------|-------------|
| `Company` | Business subscribing to the platform |
| `User` | Includes SuperAdmin, OfficeUser, and Clients |
| `BankAccount` | Bank accounts for the company |
| `MpesaAgent` | M-Pesa agent accounts |
| `CashAccount` | Cash holdings (KES and USD) |
| `Transaction` | Double-entry bookkeeping transactions |
| `Expense` | Business expenses |
| `ExpenseCategory` | Categories for expenses |
| `ExchangeRate` | KES/USD exchange rates |
| `Invoice` | Invoice templates |
| `Reconciliation` | Account reconciliation records |

### Supporting Entities

| Entity | Description |
|--------|-------------|
| `OtpCode` | OTP for authentication |
| `UserSession` | Refresh token sessions |
| `LoginHistory` | Login tracking |
| `AuditLog` | Action audit trail |
| `NotificationLog` | WhatsApp message logs |
| `SystemLog` | Error and monitoring logs |
| `SecurityAlert` | Security event tracking |
| `SubscriptionPayment` | Platform subscription payments |
| `BlockedIP` | IP blocking for security |

## API Endpoints

### Authentication

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/auth/login` | Unified login for all user types |
| POST | `/api/auth/verify-otp` | Verify OTP (SuperAdmin/OfficeUser) |
| POST | `/api/auth/refresh` | Refresh access token |
| POST | `/api/auth/logout` | Logout |

### Companies (SuperAdmin)

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/company` | Create new company |
| GET | `/api/company` | List all companies |
| GET | `/api/company/{id}` | Get company details |
| PUT | `/api/company/{id}` | Update company |
| POST | `/api/company/{id}/activate` | Activate company |
| POST | `/api/company/{id}/deactivate` | Deactivate company |
| POST | `/api/company/{id}/reset-password` | Reset password |

### Clients (OfficeUser)

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/client` | Create new client |
| GET | `/api/client` | List all clients |
| GET | `/api/client/{id}` | Get client details |
| PUT | `/api/client/{id}` | Update client |
| POST | `/api/client/{id}/convert` | Convert to permanent |
| DELETE | `/api/client/{id}` | Delete client |
| GET | `/api/client/stats` | Get client statistics |
| GET | `/api/client/{id}/statement` | Get client statement |

### Transactions (OfficeUser)

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/transaction` | Create transaction |
| GET | `/api/transaction` | List transactions |
| GET | `/api/transaction/{id}` | Get transaction |
| PUT | `/api/transaction/{id}` | Update transaction |
| DELETE | `/api/transaction/{id}` | Delete transaction |
| GET | `/api/transaction/today` | Today's summary |
| GET | `/api/transaction/recent` | Recent transactions |

### Bank Accounts (OfficeUser)

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/bank` | Create bank account |
| GET | `/api/bank` | List bank accounts |
| GET | `/api/bank/{id}` | Get account details |
| PUT | `/api/bank/{id}` | Update account |
| DELETE | `/api/bank/{id}` | Delete account |
| GET | `/api/bank/stats` | Get statistics |
| GET | `/api/bank/{id}/statement` | Get account statement |

### M-Pesa Agents (OfficeUser)

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/mpesa` | Create M-Pesa agent |
| GET | `/api/mpesa` | List agents |
| GET | `/api/mpesa/{id}` | Get agent details |
| PUT | `/api/mpesa/{id}` | Update agent |
| DELETE | `/api/mpesa/{id}` | Delete agent |
| GET | `/api/mpesa/stats` | Get statistics |
| GET | `/api/mpesa/{id}/statement` | Get statement |

### Cash Accounts (OfficeUser)

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/cash` | List cash accounts |
| GET | `/api/cash/stats` | Get statistics |
| GET | `/api/cash/statement/{currency}` | Get statement |

### Expenses (OfficeUser)

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/expense/category` | Create expense category |
| GET | `/api/expense/category` | List categories |
| PUT | `/api/expense/category/{id}` | Update category |
| DELETE | `/api/expense/category/{id}` | Delete category |
| POST | `/api/expense` | Create expense |
| GET | `/api/expense` | List expenses |
| GET | `/api/expense/{id}` | Get expense |
| GET | `/api/expense/stats` | Get statistics |

### Exchange Rates (OfficeUser)

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/exchange` | Set exchange rate |
| GET | `/api/exchange/current` | Get current rate |
| GET | `/api/exchange/history` | Get rate history |
| POST | `/api/exchange/convert` | Convert currency |
| POST | `/api/exchange/transaction` | Create exchange transaction |

### Invoices (OfficeUser)

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/invoice` | Create invoice |
| GET | `/api/invoice` | List invoices |
| GET | `/api/invoice/{id}` | Get invoice |
| PUT | `/api/invoice/{id}/status` | Update status |
| DELETE | `/api/invoice/{id}` | Delete invoice |
| GET | `/api/invoice/{id}/pdf` | Generate PDF |

### Reconciliation (OfficeUser)

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/reconciliation` | Create reconciliation |
| GET | `/api/reconciliation` | List reconciliations |
| GET | `/api/reconciliation/{id}` | Get reconciliation |
| POST | `/api/reconciliation/{id}/complete` | Complete reconciliation |
| GET | `/api/reconciliation/accounts` | Get accounts with stats |
| GET | `/api/reconciliation/account/{type}/{id}/transactions` | Account transactions |
| GET | `/api/reconciliation/account/{type}/{id}/summary` | Account summary |
| PUT | `/api/reconciliation/transaction/{id}` | Reconcile transaction |
| PUT | `/api/reconciliation/bulk` | Bulk reconcile |

### Reports (OfficeUser)

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/report/daily` | Daily report |
| GET | `/api/report/transactions` | Transaction report |
| GET | `/api/report/client-balances` | Client balance report |
| GET | `/api/report/account-summary` | Account summary report |

### Dashboard (OfficeUser)

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/dashboard` | Office user dashboard |

### Admin (SuperAdmin)

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/admin/dashboard` | Basic dashboard |
| GET | `/api/admin/dashboard/extended` | Extended dashboard |
| GET | `/api/admin/companies` | Get all companies with stats |
| GET | `/api/admin/companies/{id}/details` | Company details |
| PUT | `/api/admin/companies/{id}/subscription` | Update subscription |
| POST | `/api/admin/companies/{id}/suspend` | Suspend company |
| POST | `/api/admin/companies/{id}/activate` | Activate company |
| POST | `/api/admin/companies/{id}/reset-password` | Reset password |
| GET | `/api/admin/system/health` | System health |
| GET | `/api/admin/security/overview` | Security overview |
| GET | `/api/admin/security/alerts` | Security alerts |
| POST | `/api/admin/security/alerts/{id}/resolve` | Resolve alert |
| POST | `/api/admin/security/block-ip` | Block IP |
| DELETE | `/api/admin/security/blocked-ips/{id}` | Unblock IP |
| GET | `/api/admin/security/ip-whitelist` | IP whitelist |
| POST | `/api/admin/security/ip-whitelist` | Add IP to whitelist |
| DELETE | `/api/admin/security/ip-whitelist/{id}` | Remove IP |
| GET | `/api/admin/financials/overview` | Financial overview |
| GET | `/api/admin/financials/payments` | Payment history |
| POST | `/api/admin/financials/payments` | Record payment |
| GET | `/api/admin/analytics/overview` | Analytics |
| GET | `/api/admin/audit-logs` | Audit logs |
| GET | `/api/admin/audit-logs/extended` | Extended audit logs |
| GET | `/api/admin/audit-logs/export` | Export audit logs |

### Client Portal

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/portal/dashboard` | Client dashboard |
| GET | `/api/portal/profile` | Get profile |
| PUT | `/api/portal/profile` | Update profile |
| GET | `/api/portal/transactions` | List transactions |
| GET | `/api/portal/transactions/{id}` | Get transaction |
| GET | `/api/portal/transactions/{id}/receipt` | Download receipt |
| GET | `/api/portal/statement` | Get statement |
| GET | `/api/portal/statement/pdf` | Download statement PDF |
| GET | `/api/portal/transactions/export` | Export CSV |
| GET | `/api/portal/alerts` | Get alerts |
| GET | `/api/portal/alerts/unread-count` | Unread count |
| POST | `/api/portal/alerts/{id}/read` | Mark as read |
| POST | `/api/portal/alerts/read-all` | Mark all read |

## Authentication Flow

### SuperAdmin / OfficeUser Login
1. Call `POST /api/auth/login` with code, phone, password
2. System returns OTP requirement
3. Call `POST /api/auth/verify-otp` with code, phone, OTP
4. System returns JWT access token and refresh token

### Client Login
1. Call `POST /api/auth/login` with code, phone, password
2. System returns JWT tokens directly (no OTP)

## Test Credentials

```
Password: Test@123

SUPER ADMIN:
  Code: SA-2026-001
  Phone: +254700000000

OFFICE USER (Company):
  Code: CO-2026-001
  Phone: +254711111111

CLIENT:
  Code: CL-2026-001
  Phone: +254733333333
```

## Enumerations

### UserRole
- `SuperAdmin` = 0
- `OfficeUser` = 1
- `Client` = 2

### ClientType
- `Permanent` = 0 (Can login, receives messages)
- `Temporary` = 1 (Accounting only)

### Currency
- `KES` = 0
- `USD` = 1

### TransactionType
- `Debit` = 0 (Money IN)
- `Credit` = 1 (Money OUT)

### AccountType
- `Cash` = 0
- `Bank` = 1
- `Mpesa` = 2
- `Client` = 3

### PaymentMethod
- `Cash` = 0
- `Bank` = 1
- `Mpesa` = 2
- `AccountTransfer` = 3

### ReconciliationStatus
- `Pending` = 0
- `Matched` = 1
- `Unmatched` = 2

### SubscriptionPlan
- `Free` = 0
- `Starter` = 1
- `Professional` = 2
- `Enterprise` = 3

### SubscriptionStatus
- `Active` = 0
- `Trial` = 1
- `Expired` = 2
- `Cancelled` = 3
- `Suspended` = 4

## Running the Application

### Prerequisites
- .NET 8 SDK
- PostgreSQL

### Development Mode
```bash
cd src/SARIFF.API
dotnet run
```

The API will be available at `http://localhost:5000`
Swagger UI: `http://localhost:5000/swagger`

### Docker
```bash
cd docker
docker-compose up -d
```

API will be available at: http://localhost:8080
Swagger UI: http://localhost:8080/swagger

## Database Schema

### Key Tables
- `Users` - All user types
- `Companies` - Company/business records
- `BankAccounts` - Bank accounts
- `MpesaAgents` - M-Pesa agents
- `CashAccounts` - Cash accounts
- `Transactions` - All financial transactions
- `Expenses` - Expense records
- `ExchangeRates` - Currency exchange rates
- `Invoices` - Invoice templates
- `Reconciliations` - Reconciliation records

## Security Features

- JWT Bearer Authentication
- OTP for SuperAdmin/OfficeUser
- Role-based Authorization
- IP Whitelist/Blacklist
- Security Alert Tracking
- Audit Logging
- Login History
- Account Lockout

## Real-time Features

- SignalR Hub for notifications
- Client can receive real-time transaction updates

## Architecture

```
SARIFF.Core        - Entities, DTOs, Enums, Interfaces
SARIFF.Infrastructure - DbContext, Services, Data
SARIFF.API         - Controllers, Middleware, Hubs
```

## Tech Stack

- ASP.NET Core 8
- PostgreSQL 16
- Entity Framework Core
- JWT Authentication
- BCrypt password hashing
- SignalR
- Serilog
- Docker
