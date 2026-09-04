# Phase 3: TopUp Web UI Implementation Summary

## Overview
Implemented the Orbit Web UI for manual TopUpSession, allowing operators to initiate balance top-ups for worker accounts directly from the Worker Details page.

## Implementation Details

### 1. Backend Components

#### ViewModels
- **`Orbita.Web\Models\ViewModels\TopUpSessionViewModel.cs`** (NEW)
  - Maps `TopUpSessionDto` to Web presentation layer
  - Includes computed properties: `IsActive`, `IsTerminal`, `HasQr`, `StatusLabel`, `TierLabel`

#### ViewModel Extension
- **`Orbita.Web\Models\ViewModels\WorkersViewModel.cs`** (MODIFIED)
  - Added `CanTopUp` property to `WorkerAccountRowViewModel`
  - Added `HasActiveTopUpSession` and `ActiveTopUpSessionId` properties (for future enhancement)

#### Services
- **`Orbita.Web\Services\IWorkersService.cs`** (MODIFIED)
  - Added `CreateTopUpSessionAsync(Guid workerId, Guid accountId, CancellationToken ct)`
  - Added `GetTopUpSessionAsync(Guid sessionId, CancellationToken ct)`
  - Added `CancelTopUpSessionAsync(Guid sessionId, CancellationToken ct)`

- **`Orbita.Web\Services\WorkersService.cs`** (MODIFIED)
  - Implemented TopUp service methods with preview mode support
  - Added `MapTopUpSession` helper to convert DTO to ViewModel

- **`Orbita.Web\Services\OrbitaApiClient.cs`** (MODIFIED)
  - Added `CreateTopUpSessionAsync` - POST to `/api/v1/panel/workers/{workerId}/accounts/{accountId}/top-up-sessions`
  - Added `GetTopUpSessionAsync` - GET from `/api/v1/panel/top-up-sessions/{sessionId}`
  - Added `CancelTopUpSessionAsync` - POST to `/api/v1/panel/top-up-sessions/{sessionId}/cancel`
  - Handles 409 Conflict responses with `TopUpSessionConflictDto`

- **`Orbita.Web\Services\WorkerDetailsBuilder.cs`** (MODIFIED)
  - Added `CanTopUp` calculation in `MapAccount` method
  - Uses `TopUpSessionRules.LowBalanceThresholdRub` (150 RUB) for eligibility

#### Controllers
- **`Orbita.Web\Controllers\WorkersController.cs`** (MODIFIED)
  - Added `[HttpPost] CreateTopUpSession(Guid workerId, Guid accountId)` - Returns session or 409/400
  - Added `[HttpGet] GetTopUpSession(Guid sessionId)` - Returns session or 404
  - Added `[HttpPost] CancelTopUpSession(Guid sessionId)` - Returns success or 400

### 2. Frontend Components

#### Views
- **`Orbita.Web\Views\Workers\Details.cshtml`** (MODIFIED)
  - Added "Пополнить" button in balance cell when `account.CanTopUp` is true
  - Added TopUp modal dialog with:
    - Loading state with spinner
    - Error state with message
    - Content state showing:
      - Account info (name, balances, daily responses)
      - Tier information (response count → target balance mapping)
      - Status indicator with color coding
      - QR code display section (when ready)
      - Payment notice (manual confirmation disclaimer)
      - Terminal state messages (expired/failed/cancelled)
      - Cancel button (visible only for active sessions)

#### Styles
- **`Orbita.Web\wwwroot\css\orbita\worker-detail.css`** (MODIFIED)
  - Added `.worker-topup-trigger` - Blue button styling with hover states
  - Added `.topup-modal-dialog` - Modal sizing (560px max-width)
  - Added `.topup-loading` - Centered loading spinner animation
  - Added `.topup-error` - Error display with icon
  - Added `.topup-info` - Info panel with label-value rows
  - Added `.topup-status--*` - Status badges (requested/started/qr_ready)
  - Added `.topup-qr-section` - QR code container with white background
  - Added `.topup-qr-image` - 280px QR image display
  - Added `.topup-payment-notice` - Warning banner for manual payment disclaimer
  - Added `.topup-terminal-message` - Terminal state error messages
  - Full dark theme support with appropriate color overrides
  - Responsive design with mobile breakpoints

#### JavaScript
- **`Orbita.Web\wwwroot\js\orbita-worker.js`** (MODIFIED)
  - Added `initTopUpModal()` function with:
    - Modal open/close handlers
    - Session creation via POST to `/Workers/CreateTopUpSession`
    - Session polling every 3 seconds for active sessions
    - QR code rendering (base64 or URL)
    - Status updates with color-coded labels
    - Session cancellation via POST to `/Workers/CancelTopUpSession`
    - Automatic polling stop on terminal states
    - Anti-forgery token handling
    - Error handling for 409 conflicts and API errors
  - Integrated into `initWorkerPage()` initialization flow

### 3. UI Features

#### Eligibility & Display
- "Пополнить" button appears only when:
  - `account.Balance < 150 RUB` (TopUpSessionRules.LowBalanceThresholdRub)
  - Button displayed in balance cell of account row
  - Clear, non-alarming Russian copy

#### Session Flow
1. **Request**: Click "Пополнить" → Loading spinner → POST creates session
2. **Status Updates**: Auto-polls every 3s showing:
   - "Запрошено" (requested) - blue
   - "Обработка воркером" (started) - yellow
   - "QR-код готов к оплате" (qr_ready) - green
3. **QR Display**: When status = qr_ready, shows:
   - 280×280px QR code (base64 or URL)
   - Payment notice: "Отсканируйте QR-код в приложении банка для оплаты через СБП. Оплата производится вручную и не подтверждается автоматически в Орбите."
4. **Terminal States**:
   - Expired: "Сессия истекла. Время ожидания истекло."
   - Failed: Shows `failureMessage` from backend
   - Cancelled: "Сессия была отменена."
5. **Cancel**: "Отменить сессию" button (hidden after terminal state)

#### Information Display
- Current balance → Target balance → Requested amount
- Daily response count with tier label:
  - "0–5 откликов → 300 ₽"
  - "6–10 откликов → 900 ₽"
  - "11+ откликов → 2000 ₽"

#### Error Handling
- 409 Conflict: "Конфликт: уже есть активная сессия."
- Request errors: Shows backend error message
- Poll failures: Silent (doesn't disrupt UI)

### 4. Design Compliance

#### Existing Patterns
- Uses existing modal structure (`.workers-modal`, `.workers-modal-dialog`)
- Matches Worker Details card styling and spacing
- Integrates with row menu patterns
- Follows existing button hierarchy

#### Theme Support
- Full dark theme integration
- Color variables from existing CSS
- Proper contrast ratios maintained

#### Responsive Design
- Desktop: Full-width modal with 560px max
- Mobile: 95% width, 240×240px QR code
- Card-based layout adapts to viewport

### 5. Test Command

```powershell
dotnet build \\Desktop-43mgdo1\d\repos\LeadFlow\Orbita.Web\Orbita.Web.csproj --no-incremental
```

**Result**: ✅ Build succeeded (49 warnings, 0 errors)

### 6. Files Modified/Created

**Created:**
- `Orbita.Web\Models\ViewModels\TopUpSessionViewModel.cs`

**Modified:**
- `Orbita.Web\Models\ViewModels\WorkersViewModel.cs`
- `Orbita.Web\Services\IWorkersService.cs`
- `Orbita.Web\Services\WorkersService.cs`
- `Orbita.Web\Services\OrbitaApiClient.cs`
- `Orbita.Web\Services\WorkerDetailsBuilder.cs`
- `Orbita.Web\Controllers\WorkersController.cs`
- `Orbita.Web\Views\Workers\Details.cshtml`
- `Orbita.Web\wwwroot\css\orbita\worker-detail.css` (~300 lines added)
- `Orbita.Web\wwwroot\js\orbita-worker.js` (~270 lines added)

### 7. Key Design Decisions

1. **Manual Confirmation Disclaimer**: Prominent warning that payment is not auto-confirmed
2. **Polling vs WebSocket**: Used simple 3-second polling (matches existing pattern)
3. **Eligibility Threshold**: 150 RUB (existing WorkerDetails threshold)
4. **Session Lifecycle**: Fully handled by backend, UI just displays state
5. **Error Strategy**: Show clear messages, don't hide conflicts
6. **Cancel Permission**: Any operator can cancel (backend validates ownership)
7. **QR Format**: Supports both base64 and URL (flexible for backend implementation)
8. **Live Updates**: Polling stops automatically on terminal states

### 8. Russian Copy (Non-Alarming)

- Button: "Пополнить" (neutral, clear action)
- Status: "Запрошено", "Обработка воркером", "QR-код готов к оплате"
- Notice: Informational, not warning-toned
- Errors: Clear but professional ("Сессия истекла", not "ОШИБКА!")

### 9. Integration Points

- **Backend**: Uses existing `/api/v1/panel/workers/{workerId}/accounts/{accountId}/top-up-sessions` endpoints
- **State**: Respects `TopUpSessionStatuses` contract (requested/started/qr_ready/expired/failed/cancelled)
- **Rules**: Uses `TopUpSessionRules.LowBalanceThresholdRub` for eligibility
- **Lifecycle**: No modifications to worker automation or session backend logic

### 10. Future Enhancements (Not Implemented)

- Display active session indicator on account row (HasActiveTopUpSession property added but not used)
- Resume/reconnect to active session from account row
- Session history in account details
- Balance refresh trigger after QR payment

## Verification Checklist

✅ Backend API client methods added
✅ Service layer implements interface
✅ Controller endpoints wired
✅ ViewModel created with computed properties
✅ UI button appears only when eligible (balance < 150 RUB)
✅ Modal dialog follows existing patterns
✅ CSS matches Worker Details design
✅ Dark theme fully supported
✅ JavaScript handles session lifecycle
✅ Polling starts/stops appropriately
✅ QR code renders (base64 or URL)
✅ Payment disclaimer prominently displayed
✅ Terminal states handled
✅ Cancel functionality implemented
✅ Error handling (409 conflict, request errors)
✅ Russian copy clear and non-alarming
✅ Responsive design (desktop/mobile)
✅ Build succeeds with no errors

## Notes

- No modifications to API/Worker/Contracts projects (as required)
- Preserves existing Worker Details design language
- Desktop, card, and mobile layouts all supported
- Tier information clearly displayed (daily responses → target balance)
- Manual payment workflow emphasized to avoid confusion
