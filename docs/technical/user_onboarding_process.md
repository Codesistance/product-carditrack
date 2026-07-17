# USER ONBOARDING PROCESS - CARDITRACK

## OVERVIEW

CardiTrack supports **two organization types** with distinct onboarding flows:
1. **Family Accounts**: Individual/family monitoring elderly relatives
2. **Business Accounts**: Care homes and healthcare facilities with staff management

---

## ONBOARDING FLOW

### **STEP 1: AUTHENTICATION (Auth0)**

Users authenticate before selecting account type:

**Authentication Options:**
- **Social Login**: Google, Microsoft, Apple, Facebook (OAuth 2.0)
- **Traditional**: Email/password (Auth0 Database)
- **Enterprise SSO**: SAML 2.0, Azure AD, Okta (Business accounts only)

**Flow:**
1. User clicks "Sign In" or "Sign Up"
2. Redirected to Auth0 Universal Login
3. Chooses authentication method (Google, Microsoft, Apple, Email, etc.)
4. Authenticates via chosen provider
5. Auth0 returns to CardiTrack with authorization code
6. System exchanges code for JWT tokens
7. System checks if user exists in database
8. **New users**: Redirect to Step 2 (Organization Creation)
9. **Existing users**: Redirect to dashboard

---

### **STEP 2: ORGANIZATION TYPE SELECTION**

**After successful Auth0 authentication**, new users select their account type:

**Family Account:**
- Individual or family monitoring elderly relatives
- Single "Viewer" role by default (role chip hidden in the family UI)
- Simplified caregiver relationship structure
- Consumer-focused pricing

**Business Account:**
- Care homes, healthcare facilities
- Multi-tier roles: Admin, Staff, Viewer
- Enterprise-level user management
- Volume pricing, Enterprise SSO available

**User Interface:**
```html
<!-- Onboarding: Choose Account Type -->
<h2>Welcome, {UserName}!</h2>
<p>What type of account would you like to create?</p>

<div class="account-type-selection">
    <button @onclick="SelectFamily">
        <h3>👨‍👩‍👧 Family Account</h3>
        <p>Monitor your elderly loved ones</p>
        <ul>
            <li>Track health patterns</li>
            <li>Get preventive alerts</li>
            <li>Peace of mind for family</li>
        </ul>
    </button>

    <button @onclick="SelectBusiness">
        <h3>🏥 Business Account</h3>
        <p>For care homes and healthcare facilities</p>
        <ul>
            <li>Manage multiple residents</li>
            <li>Staff access control</li>
            <li>Enterprise SSO</li>
        </ul>
    </button>
</div>
```

**System Action:**
- Creates `Organization` entity with selected type
- Sets `IsActive = true`, captures `CreatedDate`
- Associates authenticated Auth0 user with new organization

---

### **STEP 3: SUBSCRIPTION INITIALIZATION**

Automatically triggered upon organization creation:

**Trial Setup:**
- **Status**: Trial (default)
- **Duration**: 30 days default (configurable up to 90 days)
- **Tier Options**:
  - **Basic Care**: Limited features, restricted CardiMembers and Users
  - **Complete Care**: Full feature set
  - **Guardian Plus**: Premium features, highest limits

**Configuration:**
- `StartDate`: Automatic (UTC)
- `TrialEndDate`: StartDate + Trial period
- `BillingCycle`: Monthly or Annual
- `Price`: $0.00 during trial
- `Currency`: USD
- `MaxCardiMembers`: Tier-dependent limit
- `MaxUsers`: Tier-dependent limit
- `Features`: JSON object with feature flags

**Database:**
- Unique constraint on `OrganizationId` (1 subscription per org)
- Status and EndDate indexed for performance

---

### **STEP 4: USER ACCOUNT CREATION**

After organization type selection, the system creates the user account record in the CardiTrack database, linking it to the Auth0 identity.

**User Information (from Auth0):**
- **Auth0UserId**: Subject ID from Auth0 (e.g., "google-oauth2|123456" or "auth0|789012")
- **Email**: Retrieved from Auth0 ID token
- **Name**: Full name from social provider or Auth0 database
- **ProfilePictureUrl**: Profile picture from social provider (Google, Microsoft, etc.)
- **EmailVerified**: Email verification status from Auth0
- **AuthProvider**: Which provider was used (Google, Microsoft, Apple, Facebook, Auth0Database, SAML)

**Additional Information (collected during onboarding):**
- **Phone**: Optional contact number (can pre-fill from Auth0 if available)
- **Organization Name**: For business accounts (e.g., "Sunshine Care Home")

**Role Assignment:**
- **Family Account**: First user receives `Admin` role; invited family members default to `Viewer`
- **Business Account**: First user receives `Admin` role; subsequent users get `Staff` or `Viewer`

#### **Auth0 Integration Architecture**

```
User Registration/Login Flow:
┌──────────────────────────────────────────────────┐
│  CardiTrack Web/Mobile App                      │
│  - Login button                                  │
│  - Social login buttons (Google, Microsoft, etc) │
└──────────────────────────────────────────────────┘
                    ↓
┌──────────────────────────────────────────────────┐
│  Auth0 Universal Login                           │
│  - Hosted authentication page                    │
│  - Supports all connection types                 │
│  - MFA enforcement                               │
│  - Passwordless options                          │
└──────────────────────────────────────────────────┘
                    ↓
        ┌───────────┴───────────┐
        ↓                       ↓
┌─────────────────┐    ┌─────────────────┐
│ Social Provider │    │ Email/Password  │
│ (Google, etc)   │    │ (Auth0 Database)│
└─────────────────┘    └─────────────────┘
        │                       │
        └───────────┬───────────┘
                    ↓
┌──────────────────────────────────────────────────┐
│  Auth0 Returns:                                  │
│  - ID Token (user info)                          │
│  - Access Token (API authorization)              │
│  - Refresh Token (session management)            │
└──────────────────────────────────────────────────┘
                    ↓
┌──────────────────────────────────────────────────┐
│  CardiTrack Backend API                          │
│  - Validates JWT token                           │
│  - Extracts Auth0 user ID (sub claim)            │
│  - Creates/updates User entity                   │
└──────────────────────────────────────────────────┘
```

#### **Database Schema Updates**

```csharp
User Entity (Updated):
├── Id: Internal database ID (UNIQUEIDENTIFIER PRIMARY KEY)
├── Auth0UserId: Auth0 subject ID (NVARCHAR(255) UNIQUE) - "auth0|123" or "google-oauth2|456"
├── Email: Email address (NVARCHAR(255) UNIQUE)
├── (no password field — credentials are Auth0-hosted)
├── Name: Full name
├── Phone: Optional contact number
├── ProfilePictureUrl: From social provider (NVARCHAR(500))
├── AuthProvider: Enum (Auth0Database, Google, Microsoft, Apple, Facebook, SAML)
├── EmailVerified: Boolean (from Auth0)
├── Role: Admin, Staff, Viewer
├── OrganizationId: Link to organization
├── IsActive: Boolean
├── CreatedDate: DateTime (UTC)
├── UpdatedDate: DateTime (UTC)
├── LastLoginDate: DateTime (UTC)
└── Auth0Metadata: JSON (custom user metadata from Auth0)

Indexes:
- UNIQUE INDEX on Auth0UserId
- UNIQUE INDEX on Email
- INDEX on OrganizationId
- INDEX on LastLoginDate
```

#### **User Registration Flow (Auth0)**

**Step 1: User Clicks "Sign Up"**
```csharp
// Frontend redirects to Auth0 Universal Login
var authUrl = $"https://{Auth0Domain}/authorize?" +
              $"response_type=code&" +
              $"client_id={Auth0ClientId}&" +
              $"redirect_uri={RedirectUri}&" +
              $"scope=openid profile email phone&" +
              $"audience={Auth0Audience}&" +
              $"state={GenerateSecureState()}";

NavigateTo(authUrl);
```

**Step 2: User Chooses Authentication Method**
- Click "Continue with Google" → OAuth to Google
- Click "Continue with Microsoft" → OAuth to Microsoft
- Click "Continue with Email" → Enter email/password

**Step 3: Auth0 Callback**
```csharp
[HttpGet("auth/callback")]
public async Task<IActionResult> HandleAuth0Callback(string code, string state)
{
    // Validate state (CSRF protection)
    if (!ValidateState(state))
        return BadRequest("Invalid state");

    // Exchange authorization code for tokens
    var tokens = await ExchangeCodeForTokens(code);

    // Validate and decode ID token (JWT)
    var userInfo = await ValidateAndDecodeIdToken(tokens.IdToken);

    // Extract Auth0 user information
    var auth0UserId = userInfo.Sub; // "auth0|123" or "google-oauth2|456"
    var email = userInfo.Email;
    var name = userInfo.Name;
    var emailVerified = userInfo.EmailVerified;
    var picture = userInfo.Picture;
    var authProvider = DetermineAuthProvider(auth0UserId);

    // Check if user exists in our database
    var user = await _userRepo.GetByAuth0UserId(auth0UserId);

    if (user == null)
    {
        // NEW USER: Redirect to onboarding flow
        // Store Auth0 info in session for onboarding
        HttpContext.Session.SetString("Auth0UserId", auth0UserId);
        HttpContext.Session.SetString("Email", email);
        HttpContext.Session.SetString("Name", name);
        HttpContext.Session.SetString("Picture", picture);
        HttpContext.Session.SetString("AuthProvider", authProvider.ToString());

        return Redirect("/onboarding/organization-setup");
    }
    else
    {
        // EXISTING USER: Log in
        await _userRepo.UpdateLastLoginDate(user.Id);

        // Create application session
        await SignInUser(user.Id, tokens.AccessToken, tokens.RefreshToken);

        return Redirect("/dashboard");
    }
}

private async Task<Auth0Tokens> ExchangeCodeForTokens(string code)
{
    var client = new HttpClient();
    var request = new FormUrlEncodedContent(new[]
    {
        new KeyValuePair<string, string>("grant_type", "authorization_code"),
        new KeyValuePair<string, string>("client_id", _auth0Config.ClientId),
        new KeyValuePair<string, string>("client_secret", _auth0Config.ClientSecret),
        new KeyValuePair<string, string>("code", code),
        new KeyValuePair<string, string>("redirect_uri", _auth0Config.RedirectUri)
    });

    var response = await client.PostAsync($"https://{_auth0Config.Domain}/oauth/token", request);
    var content = await response.Content.ReadAsStringAsync();

    return JsonSerializer.Deserialize<Auth0Tokens>(content);
}
```

**Step 4: Complete Onboarding (New Users)**
```csharp
[HttpPost("onboarding/complete")]
public async Task<IActionResult> CompleteOnboarding(OnboardingRequest request)
{
    var auth0UserId = HttpContext.Session.GetString("Auth0UserId");
    var email = HttpContext.Session.GetString("Email");
    var name = HttpContext.Session.GetString("Name");
    var picture = HttpContext.Session.GetString("Picture");
    var authProvider = Enum.Parse<AuthProvider>(HttpContext.Session.GetString("AuthProvider"));

    // Create organization
    var org = new Organization
    {
        Name = request.OrganizationName,
        Type = request.OrganizationType
    };
    await _orgRepo.Create(org);

    // Create subscription (Trial)
    await _subscriptionService.CreateTrial(org.Id);

    // Create user in our database
    var user = new User
    {
        Auth0UserId = auth0UserId,
        Email = email,
        // No password field — credentials are Auth0-hosted
        Name = name,
        Phone = request.Phone,
        ProfilePictureUrl = picture,
        AuthProvider = authProvider,
        EmailVerified = true, // Auth0 handles verification
        Role = request.OrganizationType == OrganizationType.Family ? UserRole.Member : UserRole.Admin,
        OrganizationId = org.Id,
        IsActive = true
    };
    await _userRepo.Create(user);

    // Sync user metadata to Auth0 (optional)
    await _auth0Service.UpdateUserMetadata(auth0UserId, new
    {
        organization_id = org.Id,
        role = user.Role.ToString()
    });

    // Create session
    await SignInUser(user.Id, accessToken, refreshToken);

    // Audit log
    await _auditLogger.LogAction(user.Id, "UserRegistered", authProvider.ToString());

    return Ok(new { userId = user.Id, organizationId = org.Id });
}
```

#### **Authentication & Authorization**

**JWT Token Validation:**
```csharp
// Startup.cs or Program.cs
services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = $"https://{Configuration["Auth0:Domain"]}/";
        options.Audience = Configuration["Auth0:Audience"];
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = $"https://{Configuration["Auth0:Domain"]}/",
            ValidateAudience = true,
            ValidAudience = Configuration["Auth0:Audience"],
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero // No tolerance for expired tokens
        };
    });

services.AddAuthorization(options =>
{
    options.AddPolicy("RequireAdmin", policy =>
        policy.RequireClaim("role", "Admin"));

    options.AddPolicy("RequireBusinessAccount", policy =>
        policy.RequireClaim("organization_type", "Business"));
});
```

**Protected API Endpoints:**
```csharp
[Authorize] // Requires valid Auth0 JWT
[HttpGet("api/dashboard")]
public async Task<IActionResult> GetDashboard()
{
    var auth0UserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    var user = await _userRepo.GetByAuth0UserId(auth0UserId);

    // ... rest of logic
}

[Authorize(Policy = "RequireAdmin")] // Admin only
[HttpPost("api/organization/settings")]
public async Task<IActionResult> UpdateSettings(OrganizationSettings settings)
{
    // ... admin-only logic
}
```

#### **Role Assignment**

- **Family Account**: `Admin` (account creator); invited family default to `Viewer`
- **Business Account**: `Admin` (first user), `Staff`, or `Viewer`

**Auth0 Rules (Custom Role Assignment):**
```javascript
// Auth0 Rule: Add roles to JWT token
function addRolesToToken(user, context, callback) {
  const namespace = 'https://carditrack.com';

  // Fetch user metadata from our API
  const userId = user.user_metadata.carditrack_user_id;

  if (userId) {
    // Add role to access token
    context.accessToken[namespace + '/role'] = user.user_metadata.role;
    context.accessToken[namespace + '/organization_id'] = user.user_metadata.organization_id;
  }

  callback(null, user, context);
}
```

#### **Security Features**

**Auth0 Provides:**
- ✅ **Secure Password Storage**: Bcrypt hashing with salt
- ✅ **Email Verification**: Automatic verification emails
- ✅ **Multi-Factor Authentication (MFA)**: SMS, Authenticator app, WebAuthn
- ✅ **Passwordless Login**: Magic links, SMS OTP
- ✅ **Breached Password Detection**: Checks against known breached passwords
- ✅ **Bot Detection**: reCAPTCHA integration
- ✅ **Anomaly Detection**: Suspicious login attempts
- ✅ **Session Management**: Automatic token refresh
- ✅ **HIPAA BAA Available**: Auth0 provides BAA for healthcare apps

**Additional Security in CardiTrack:**
- ✅ **Token Encryption at Rest**: Store Auth0 refresh tokens encrypted
- ✅ **Audit Logging**: Log all authentication events
- ✅ **Session Timeout**: 15-minute idle timeout
- ✅ **IP Whitelisting**: For business accounts (optional)
- ✅ **Role-Based Access Control (RBAC)**: Enforced at API level

#### **Social Login User Experience**

**Login Page UI:**
```html
<!-- Blazor Login Component -->
<div class="auth-container">
    <h2>Sign in to CardiTrack</h2>

    <!-- Social Login Buttons -->
    <div class="social-login">
        <button @onclick="LoginWithGoogle" class="btn-social btn-google">
            <img src="/icons/google.svg" alt="Google" />
            Continue with Google
        </button>

        <button @onclick="LoginWithMicrosoft" class="btn-social btn-microsoft">
            <img src="/icons/microsoft.svg" alt="Microsoft" />
            Continue with Microsoft
        </button>

        <button @onclick="LoginWithApple" class="btn-social btn-apple">
            <img src="/icons/apple.svg" alt="Apple" />
            Continue with Apple
        </button>
    </div>

    <div class="divider">
        <span>OR</span>
    </div>

    <!-- Email/Password Login -->
    <button @onclick="LoginWithEmail" class="btn-primary">
        Continue with Email
    </button>

    <p class="terms">
        By continuing, you agree to our
        <a href="/terms">Terms of Service</a> and
        <a href="/privacy">Privacy Policy</a>
    </p>
</div>

@code {
    private void LoginWithGoogle()
    {
        var authUrl = BuildAuth0Url(connection: "google-oauth2");
        NavigationManager.NavigateTo(authUrl, forceLoad: true);
    }

    private void LoginWithMicrosoft()
    {
        var authUrl = BuildAuth0Url(connection: "windowslive");
        NavigationManager.NavigateTo(authUrl, forceLoad: true);
    }

    private void LoginWithApple()
    {
        var authUrl = BuildAuth0Url(connection: "apple");
        NavigationManager.NavigateTo(authUrl, forceLoad: true);
    }

    private void LoginWithEmail()
    {
        var authUrl = BuildAuth0Url(connection: "Username-Password-Authentication");
        NavigationManager.NavigateTo(authUrl, forceLoad: true);
    }

    private string BuildAuth0Url(string connection)
    {
        return $"https://{Auth0Domain}/authorize?" +
               $"response_type=code&" +
               $"client_id={Auth0ClientId}&" +
               $"connection={connection}&" +
               $"redirect_uri={RedirectUri}&" +
               $"scope=openid profile email phone&" +
               $"audience={Auth0Audience}&" +
               $"state={GenerateSecureState()}";
    }
}
```

#### **Account Linking**

Users can link multiple authentication methods to one account:

**Scenario**: User signs up with Google, later wants to add Microsoft login
```csharp
[Authorize]
[HttpPost("api/auth/link-account")]
public async Task<IActionResult> LinkAccount(string provider)
{
    var primaryAuth0UserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    // Generate linking authorization URL
    var linkingUrl = $"https://{_auth0Config.Domain}/authorize?" +
                     $"response_type=code&" +
                     $"client_id={_auth0Config.ClientId}&" +
                     $"connection={provider}&" +
                     $"redirect_uri={_auth0Config.LinkingRedirectUri}&" +
                     $"scope=openid profile email&" +
                     $"state={primaryAuth0UserId}"; // Pass primary user ID

    return Ok(new { linkingUrl });
}

[HttpGet("auth/link-callback")]
public async Task<IActionResult> HandleLinkCallback(string code, string state)
{
    var primaryAuth0UserId = state;

    // Exchange code for secondary account tokens
    var tokens = await ExchangeCodeForTokens(code);
    var secondaryUserInfo = await ValidateAndDecodeIdToken(tokens.IdToken);

    // Link accounts in Auth0
    await _auth0ManagementClient.LinkAccountsAsync(
        primaryUserId: primaryAuth0UserId,
        secondaryUserId: secondaryUserInfo.Sub
    );

    return Redirect("/settings/linked-accounts");
}
```

#### **System Actions**

- Redirects to Auth0 Universal Login
- User authenticates via chosen method (email/password or social)
- Auth0 returns authorization code
- Backend exchanges code for JWT tokens
- Validates JWT signature and claims
- Extracts `Auth0UserId` (sub claim)
- Creates or retrieves `User` entity
- Links to `OrganizationId`
- Sets `IsActive = true`, `CreatedDate`, `UpdatedDate`
- Updates `LastLoginDate`
- Stores encrypted Auth0 refresh token for session management
- Logs authentication event in `AuditLog`

---

### **STEP 5: CARDIMEMBER SETUP**

Users add the elderly person(s) to monitor:

**Personal Information:**
- **Name**: Full name of monitored individual
- **Date of Birth**: For age calculations, baseline adjustments
- **Gender**: Male, Female, Other, PreferNotToSay
- **Phone**: Emergency contact number
- **EmergencyContact**: Additional contact info (JSON)
- **Medical Notes**: Encrypted field for health context

**Relationship Definition:**
```
UserCardiMembers (Many-to-Many Linking Table)
├── RelationshipType: Self, Parent, Spouse, Grandparent, Sibling, Child, Other
├── IsPrimaryCaregiver: Boolean (primary caregiver flag)
├── CanViewHealthData: Boolean
├── CanReceiveAlerts: Boolean
├── CanManageDevices: Boolean
└── Permissions: JSON (granular access control)
```

**Key Features:**
- One CardiMember can have multiple caregivers
- One caregiver can monitor multiple CardiMembers
- Permission scoping per relationship
- Primary caregiver designation for escalation

**Encryption:**
- `MedicalNotes` encrypted using AES-256-GCM
- Ensures HIPAA compliance for PHI at rest

---

### **STEP 6: DEVICE CONNECTION**

Users connect health monitoring devices:

**Device Selection:**
Supports 8+ device types:
- Fitbit
- AppleWatch
- Garmin
- Samsung
- Withings
- Oura
- Whoop
- Other

**OAuth Authorization Flow:**

```
1. User clicks "Connect Fitbit"
   ↓
2. System generates secure state token
   ↓
3. Redirect to Fitbit OAuth page
   https://www.fitbit.com/oauth2/authorize?
     response_type=code
     client_id={ClientId}
     redirect_uri={RedirectUri}
     scope=activity heartrate sleep profile
     state={CardiMemberId}:{Token}
   ↓
4. User approves permissions on Fitbit
   ↓
5. Fitbit redirects to callback with authorization code
   ↓
6. System exchanges code for access/refresh tokens
   POST https://api.fitbit.com/oauth2/token
   Authorization: Basic {base64(clientId:clientSecret)}
   Body: grant_type=authorization_code&code={code}&redirect_uri={RedirectUri}
   ↓
7. Save encrypted tokens to database (Azure SQL, DeviceConnections)
   ↓
8. Register Fitbit webhook subscription (Subscriptions API) + trigger initial history backfill
   ↓
9. Notify family: "Fitbit Connected!"
```

**Database Storage:**
```csharp
DeviceConnection Entity:
├── CardiMemberId: Link to monitored individual
├── DeviceType: Enum (Fitbit, AppleWatch, etc.)
├── AccessToken: Encrypted (AES-256-GCM)
├── RefreshToken: Encrypted (AES-256-GCM)
├── TokenExpiry: DateTime (UTC)
├── ConnectionStatus: Pending, Connected, Disconnected, TokenExpired, AuthError, SyncError
└── LastSyncDate: DateTime (UTC) — timestamp of last webhook event received
```

**Permission Scoping:**
Fitbit API scopes requested:
- `activity`: Steps, distance, floors, active minutes
- `heartrate`: Resting HR, HR zones, intraday HR (if approved)
- `sleep`: Duration, efficiency, sleep stages
- `profile`: User info, timezone

**Data Ingestion & Token Management:**
- **Webhook push** — Fitbit Subscriptions API notifies CardiTrack on new data; events flow through Event Hubs into the AI pipeline (see [llm_design.md](../llm_design.md)). No polling sync job.
- **Token refresh** — `CardiTrack.Worker` proactively refreshes OAuth tokens before expiry (cron-driven, Cronos)
- Retry logic with exponential backoff
- `device_disconnected` alert to family if no events for >2 hours during waking hours

---

### **STEP 7: NOTIFICATION PREFERENCES**

Configure alert system:

**Alert Types:**
1. **Inactivity Alerts**: Steps < 50% baseline for 2+ days
2. **Heart Rate Alerts**: Resting HR >15% above baseline for 3+ days
3. **Sleep Disruption**: Sleep efficiency < 70% for 5+ days
4. **Sudden Pattern Break**: No morning activity by 11am
5. **Long-term Trends**: Declining mobility over 4 weeks

**Alert Severity Levels:**
- **Green**: Informational, no action needed
- **Yellow**: Minor deviation, "worth a call"
- **Orange**: Concerning pattern, "consider doctor visit"
- **Red**: Urgent, "please check on them"

**Notification Channels:**
- **SMS**: Via Twilio (requires BAA for HIPAA)
- **Email**: Via SendGrid (requires BAA for HIPAA)
- **Push Notifications**: Mobile app (iOS/Android via .NET MAUI)
- **In-App**: Dashboard notifications with real-time SignalR updates

**Customization:**
- Alert sensitivity tuning (z-score thresholds)
- Quiet hours (no alerts during sleep)
- Escalation rules (Red alerts → SMS, Yellow → Email)
- Per-alert-type enable/disable

---

### **STEP 8: BASELINE ESTABLISHMENT**

System begins learning normal patterns:

**Learning Period:**
- **Duration**: 30 days default (configurable up to 90 days)
- **Frequency**: Recalculated weekly after initial baseline

**Pattern Baseline Calculation:**
```csharp
PatternBaseline Entity:
├── CardiMemberId: Link to monitored individual
├── CalculatedDate: When baseline was computed
├── PeriodDays: 30, 60, or 90 days
│
├── Activity Patterns:
│   ├── AvgSteps: Mean daily steps
│   ├── StdDevSteps: Standard deviation (for z-score)
│   ├── AvgActiveMinutes: Mean active minutes/day
│   └── StepsByDayOfWeek: JSON array [Mon: 5000, Tue: 4800, ...]
│
├── Heart Rate Patterns:
│   ├── AvgRestingHeartRate: Mean resting HR
│   ├── StdDevHeartRate: Standard deviation
│   └── MaxHeartRateObserved: Highest HR recorded
│
└── Sleep Patterns:
    ├── AvgSleepMinutes: Mean sleep duration
    ├── TypicalBedtime: Time (HH:MM)
    ├── TypicalWakeTime: Time (HH:MM)
    └── AvgSleepEfficiency: Mean efficiency %
```

**AI/ML Pattern Analysis:**
- **Algorithm**: statistical thresholds at R1; SSA-LSTM + MedGemma from R2 (see [llm_design.md](../llm_design.md))
- **Z-Score Calculation**: (TodayValue - Baseline) / StdDev
- **Threshold**: |Z-Score| > 2.0 triggers alert
- **Day-of-Week Awareness**: Monday vs Saturday different baselines
- **Multi-Day Trend Detection**: 7-day rolling average analysis

**Onboarding Experience:**
```
Days 1-30: "Learning your patterns..."
  - Dashboard shows: "Building your baseline (Day 15 of 30)"
  - Limited alerts (only severe anomalies)
  - Educational tips about what we're learning

Day 31: "Baseline established!"
  - Full AI anomaly detection activated
  - Alert system fully operational
  - Email notification to family
```

---

### **STEP 9: TRIAL PERIOD & CONVERSION**

**Trial Experience:**
- Full access to all tier features
- No payment information required upfront
- `SubscriptionStatus = Trial`
- `TrialEndDate` tracked in database

**Trial End Reminders:**
- **Day -14**: "Your trial ends in 2 weeks"
- **Day -7**: "Trial ends in 1 week, select your plan"
- **Day -3**: "3 days left, upgrade now to avoid interruption"
- **Day 0**: Convert or suspend service

**Conversion Flow:**
```
1. User selects subscription tier
   ├── Basic Care: $8/month
   ├── Complete Care: $15/month
   └── Guardian Plus: $29.99/month

2. Payment information collection
   ├── Credit card via Stripe/PayPal
   ├── PCI-DSS compliant (use tokenization)
   └── Auto-renewal enabled by default

3. Subscription activation
   ├── Status: Trial → Active
   ├── StartDate: Today
   ├── EndDate: Today + Billing cycle (Monthly/Annual)
   ├── Price: Tier price
   └── NextBillingDate: EndDate

4. Invoice generation
   ├── Email receipt
   └── Store in Invoices table (HIPAA audit trail)

5. Feature limits enforcement
   ├── Apply tier-specific MaxCardiMembers
   ├── Apply tier-specific MaxUsers
   └── Enable/disable features per JSON config
```

**Failed Conversion:**
- **Status**: Trial → Suspended
- **Grace Period**: 7 days read-only access
- **Data Retention**: 90 days before soft delete
- **Re-activation**: Allow within 90 days

---

## SECURITY & COMPLIANCE FEATURES

### **HIPAA Compliance**

**Data Encryption:**
- **At Rest**: Azure SQL TDE (Transparent Data Encryption)
  - OAuth tokens: AES-256-GCM
  - Medical notes: AES-256-GCM
- **In Transit**: HTTPS/TLS 1.2+ for all connections
- **Backups**: Automatic encrypted backups (Azure)

**Access Controls:**
- **Role-Based Access Control (RBAC)**: Admin, Staff, Viewer roles
- **Relationship-Scoped Access**: Caregivers only see assigned CardiMembers
- **Multi-Factor Authentication (MFA)**: For admin accounts (recommended)
- **Automatic Session Timeout**: 15 minutes idle timeout

**Audit Trail:**
```csharp
AuditLog Entity (90-day minimum retention):
├── UserId: Who accessed
├── CardiMemberId: Whose data was accessed
├── Action: ViewDashboard, ViewAlert, ExportData, etc.
├── Timestamp: When (UTC)
├── IpAddress: From where
├── UserAgent: Browser/device info
└── DataAccessed: JSON (specific fields viewed)
```

**User Rights (HIPAA Required):**
- **Right to Access**: Export all PHI as encrypted PDF
- **Right to Amend**: Request data corrections
- **Right to Accounting**: View access log
- **Right to Restrict**: Limit data sharing (optional feature)

**Business Associate Agreements (BAAs):**
- ✅ **Auth0**: Provides BAA for HIPAA-compliant authentication
- ✅ **Azure (Microsoft)**: App Service, SQL, Functions
- ✅ **Twilio**: SMS alerts
- ✅ **SendGrid**: Email alerts
- ❌ **Fitbit**: Does NOT provide BAA (user consent model)

### **Data Protection**

**Soft Deletes:**
- `IsActive` flag on Organizations, Users, CardiMembers
- No hard deletions to preserve audit trail
- 90-day retention before purging

**No Foreign Key Constraints:**
- Application-level referential integrity
- Allows flexible permission enforcement
- Prevents cascading deletes that violate audit requirements

**Authentication Security (Auth0):**
- **Password Hashing**: Bcrypt with salt (managed by Auth0)
- **Password Complexity**: Enforced by Auth0 (8+ chars, mixed case, numbers, symbols)
- **No Local Password Storage**: Passwords NEVER stored in CardiTrack database
- **Breached Password Detection**: Auth0 checks against known breached password databases
- **Auth0 Refresh Tokens**: Stored encrypted (AES-256-GCM) in CardiTrack database
- **JWT Tokens**: Short-lived access tokens (1 hour expiry)
- **No Credentials in Logs**: Auth tokens never logged or stored in audit trails

---

## TECHNICAL ARCHITECTURE SUMMARY

### **Database Tables Involved in Onboarding**

```
Organizations
├── Id, Name, Type (Family/Business), IsActive, CreatedDate, UpdatedDate

Users (Updated with Auth0 Integration)
├── Id, Auth0UserId (UNIQUE), Email (UNIQUE) — no password column (Auth0-hosted)
├── Name, Phone, ProfilePictureUrl, AuthProvider (Enum), EmailVerified
├── Role, OrganizationId, Auth0Metadata (JSON)
└── IsActive, CreatedDate, UpdatedDate, LastLoginDate

CardiMembers
├── Id, OrganizationId, Name, DateOfBirth, Gender, Phone, EmergencyContact
├── MedicalNotes (ENCRYPTED), IsActive, CreatedDate, UpdatedDate

UserCardiMembers (Relationship Table)
├── Id, UserId, CardiMemberId, RelationshipType, IsPrimaryCaregiver
├── CanViewHealthData, CanReceiveAlerts, CanManageDevices, Permissions (JSON)
└── CreatedDate

Subscriptions
├── Id, OrganizationId (UNIQUE), Tier, Status, StartDate, EndDate, TrialEndDate
├── Price, Currency, BillingCycle, MaxCardiMembers, MaxUsers, Features (JSON)
└── CreatedDate, UpdatedDate

DeviceConnections
├── Id, CardiMemberId, DeviceType, AccessToken (ENCRYPTED), RefreshToken (ENCRYPTED)
├── TokenExpiry, ConnectionStatus, LastSyncDate, SyncFrequencyMinutes
└── CreatedDate

Devices (Reference Table)
├── Id, DeviceType, Manufacturer, ModelName, Capabilities (JSON)
└── OAuthConfig (JSON: clientId, scopes, endpoints)

ActivityLog (Populated Post-Onboarding)
├── Id, CardiMemberId, DeviceConnectionId, Date, Steps, HeartRate, Sleep, SpO2
└── CreatedDate

PatternBaseline (Created After 30 Days)
├── Id, CardiMemberId, CalculatedDate, PeriodDays, AvgSteps, StdDevSteps
├── AvgRestingHeartRate, StdDevHeartRate, AvgSleepMinutes, StepsByDayOfWeek (JSON)

Alerts (Generated by AI Engine)
├── Id, CardiMemberId, AlertType, Severity, Title, Message, TriggeredDate
├── AcknowledgedDate, AcknowledgedByUserId, IsResolved

AuditLog (HIPAA Compliance)
├── Id, UserId, CardiMemberId, Action, Timestamp, IpAddress, UserAgent
└── DataAccessed (JSON)
```

### **Background Jobs (CardiTrack.Worker — Cronos)**

Non-AI background jobs run in `CardiTrack.Worker` as `CronBackgroundService` subclasses (see [apps/worker/](../apps/worker/readme.md)). Data ingestion itself is webhook-driven — there is no sync polling job.

```csharp
// Cron expressions are 6-field (Cronos IncludeSeconds), configured in appsettings

// Hourly — refresh OAuth tokens expiring within the next hour
public class TokenRefreshWorker : CronBackgroundService      // "0 0 * * * *"

// Weekly (Sundays 02:00 UTC) — recalculate pattern baselines
public class BaselineRecalculationWorker : CronBackgroundService // "0 0 2 * * SUN"

// Daily (midnight UTC) — trial expiration reminders (14/7/3 days)
public class TrialExpirationWorker : CronBackgroundService   // "0 0 0 * * *"

// Daily — data retention/cleanup (report expiry, audit archive tiering)
public class RetentionWorker : CronBackgroundService         // "0 30 0 * * *"
```

> The AI pipeline's scheduled jobs (5-minute aggregation, daily predictive batch, digests) are Azure Functions timers — see [llm_design.md](../llm_design.md). They are not Worker jobs.

---

## USER ONBOARDING CHECKLIST

### **Family Member Actions:**
- [ ] **Choose authentication method** (Google, Microsoft, Apple, Facebook, or Email/Password)
- [ ] **Authenticate via Auth0** Universal Login
- [ ] **Verify email** (automatic with social login, manual with email/password)
- [ ] **Complete onboarding**: Select organization type (Family/Business)
- [ ] **Add CardiMember profile** (name, DOB, gender, medical notes)
- [ ] **Define relationship** (Parent, Spouse, Grandparent, etc.)
- [ ] **Send device connection invitation** to elderly person
- [ ] **Wait for elderly person** to authorize Fitbit/Apple Watch/Garmin
- [ ] **Configure notification preferences** (SMS/Email/Push)
- [ ] **Review baseline learning progress** (30 days)
- [ ] **Select subscription tier** before trial ends
- [ ] **Optional**: Link additional auth providers (e.g., add Microsoft after signing up with Google)

### **Elderly Person Actions:**
- [ ] Receive invitation (Email + SMS)
- [ ] Click secure connection link
- [ ] Review privacy notice (what family will see)
- [ ] Click "Connect Fitbit" button
- [ ] Log into Fitbit account (OAuth)
- [ ] Approve data access permissions
- [ ] Confirm connection success

### **System Actions (Automated):**
- [ ] **Redirect to Auth0** Universal Login
- [ ] **Authenticate user** via chosen provider (Google, Microsoft, Apple, Email/Password)
- [ ] **Receive Auth0 callback** with authorization code
- [ ] **Exchange code for JWT tokens** (ID token, Access token, Refresh token)
- [ ] **Validate JWT signature** and claims
- [ ] **Extract Auth0UserId** (sub claim) and user info
- [ ] **Check if user exists** in CardiTrack database
- [ ] **If new user**: Store Auth0 info in session, redirect to onboarding
- [ ] **Create Organization** entity
- [ ] **Initialize Subscription** (Trial status)
- [ ] **Create User account** with Auth0UserId (NO password storage)
- [ ] **Sync user metadata** to Auth0 (organization_id, role)
- [ ] **Send welcome email** (via SendGrid)
- [ ] **Link User to CardiMember** (UserCardiMembers table)
- [ ] **Generate secure device connection token** (24-hour expiry)
- [ ] **Send device connection invitation** to elderly person
- [ ] **Validate connection token** on Fitbit/device callback
- [ ] **Exchange OAuth code for device access/refresh tokens**
- [ ] **Encrypt and store device tokens** (AES-256-GCM)
- [ ] **Register device webhook subscription** (Fitbit Subscriptions API) + trigger initial history backfill
- [ ] **Notify family** of successful device connection
- [ ] **Calculate baseline** after 30 days
- [ ] **Activate AI anomaly detection**
- [ ] **Log authentication events** in AuditLog
- [ ] **Send trial expiration reminders** (14, 7, 3 days)
- [ ] **Convert to Active or Suspended** on trial end

---

## SUCCESS METRICS

**Onboarding Completion Rate:**
- Target: >80% of signups complete full onboarding
- Track drop-off at each step:
  - Account creation → CardiMember setup: >95%
  - CardiMember setup → Device connection invitation sent: >90%
  - Invitation sent → Device connected: >70% (hardest step)
  - Device connected → Baseline established: >95%
  - Trial → Paid conversion: >60%

**Time to Value:**
- Account creation → First device connection: <24 hours
- Device connection → First webhook data received: <30 minutes
- First sync → First alert: Varies (0-30 days)
- Trial start → Baseline established: 30 days

**Key Drop-Off Points:**
- Elderly person clicks invitation link: 80%
- Elderly person completes Fitbit OAuth: 75%
- Family reviews dashboard within 7 days: 85%
- Trial converts to paid: 60%

---

## ONBOARDING ENHANCEMENTS (FUTURE)

**Gamification:**
- Progress bar: "3 of 5 steps complete"
- Checklist with completion badges
- Email nudges for incomplete onboarding

**Guided Tour:**
- Interactive dashboard walkthrough
- Video tutorials for device connection
- FAQ chatbot for common questions

**White-Glove Onboarding (Premium Tier):**
- Dedicated onboarding specialist
- Phone call to elderly person to assist with Fitbit connection
- Custom baseline tuning based on medical history

**Multi-Language Support:**
- Spanish, Mandarin, French (high elderly populations)
- Localized date/time formats
- Cultural sensitivity in messaging

---

## SUMMARY

The CardiTrack user onboarding process is designed to be **secure, compliant, and user-friendly**, balancing the needs of family caregivers (ease of use) with elderly users (simplicity, privacy) while maintaining HIPAA compliance and AI-powered preventive health monitoring capabilities.

**Key Success Factors:**
1. **Low friction**: 5-step process, 10-minute completion
2. **Flexible authentication**: Email/password, Google, Microsoft, Apple, Facebook, Enterprise SSO
3. **Auth0 integration**: Secure, HIPAA-compliant authentication backend
4. **Security first**: Encryption, HIPAA compliance, audit trails, MFA support
5. **Privacy transparency**: Clear explanation of what family sees
6. **Immediate value**: Dashboard goes live within 30 minutes
7. **Flexible pricing**: Trial period, multiple tiers, BYOD option

**Authentication Options:**
- **Social Login**: Google, Microsoft, Apple, Facebook (OAuth 2.0)
- **Traditional**: Email/password (Auth0 Database)
- **Enterprise SSO**: SAML 2.0, Azure AD, Okta (Business accounts)
- **Account Linking**: Users can link multiple providers to one account
- **MFA**: SMS, Authenticator app, WebAuthn support
- **Passwordless**: Magic links, SMS OTP available

**Critical Path:**
Auth0 Authentication (Social/Email Login) → Organization Type Selection (Family/Business) → Subscription Trial Initialization → User Account Creation → CardiMember Setup → Device Connection (Fitbit/Apple Watch/Garmin OAuth) → Notification Preferences → Baseline Learning (30 days) → Paid Conversion

**Onboarding Steps Summary:**
1. **Authentication**: Choose Google, Microsoft, Apple, Facebook, or Email/Password via Auth0
2. **Organization Type**: Select Family or Business account
3. **Subscription**: Automatic trial initialization (30-90 days)
4. **User Account**: Create database record linked to Auth0 identity
5. **CardiMember**: Add elderly person to monitor
6. **Device**: Connect Fitbit/Apple Watch/Garmin via OAuth
7. **Notifications**: Configure alert preferences
8. **Baseline**: AI learns patterns over 30 days
9. **Conversion**: Choose paid tier at trial end

**Auth0 Benefits:**
- ✅ No password storage in CardiTrack database
- ✅ Breached password detection
- ✅ Automatic email verification
- ✅ Enterprise SSO for healthcare organizations
- ✅ HIPAA BAA available
- ✅ Bot detection and anomaly detection
- ✅ Session management and token refresh
- ✅ Profile pictures from social providers

---

**END OF ONBOARDING DOCUMENTATION**
