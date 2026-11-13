// --- Global Scope ---
// NOTE: MQL4 (build 600+) strings are Unicode (UTF-16). A "string" parameter is passed
// to a DLL as wchar_t* (LPCWSTR), so the W version of the API is the correct one.
// Do NOT switch to CreateEventA: it would read only the first character of the name.
// MT4 is a 32-bit terminal, so handles fit in an int.
#import "kernel32.dll"
int CreateEventW(int lpEventAttributes, int bManualReset, int bInitialState, string lpName);
int CreateMutexW(int lpMutexAttributes, int bInitialOwner, string lpName);
int OpenMutexW(int dwDesiredAccess, int bInheritHandle, string lpName);
int SetEvent(int hEvent);
int CloseHandle(int hObject);
int GetLastError(); // Import Windows GetLastError for accurate system error codes
#import

// --- Input Parameters ---
input string InstanceID = "MT4_01"; // Must match the key in Lifeguard config.json (MT4Instances) EXACTLY.
                                    // Each MT4 terminal MUST use a unique InstanceID: a copied terminal that
                                    // keeps the old ID makes Lifeguard see the other instance as dead.
input int HeartbeatInterval = 3;    // Heartbeat timer in seconds. Keep it well below Lifeguard
                                    // TimeoutMilliseconds (default 10 s), e.g. 3 s = 3 beats per timeout

// Global Variables
int hEventHandle = 0;               // Store event handle to avoid repeated creation
int hOwnerMutex = 0;                // Proves this InstanceID is used by only one EA in this Windows session
bool errorLogged = false;           // Prevent log spamming in OnTimer

// Whitespace that is easy to paste by accident and hard to see
bool IsBlankChar(ushort c)
{
    return(c == ' ' || c == '\t' || c == '\r' || c == '\n' ||
           c == 0x00A0 ||   // no-break space
           c == 0x3000);    // full-width (ideographic) space, e.g. from a Chinese IME
}

// Characters that are invisible anywhere in the string
bool IsInvisibleChar(ushort c)
{
    return(c == 0x200B || c == 0x200C || c == 0x200D ||   // zero-width space / non-joiner / joiner
           c == 0xFEFF);                                  // BOM / zero-width no-break space
}

bool HasInvisibleOrEdgeSpace(string text)
{
    int len = StringLen(text);
    if(len == 0) return(false);

    if(IsBlankChar(StringGetCharacter(text, 0)) || IsBlankChar(StringGetCharacter(text, len - 1)))
        return(true);

    for(int i = 0; i < len; i++)
        if(IsInvisibleChar(StringGetCharacter(text, i)))
            return(true);

    return(false);
}

// --- Initialization ---
int OnInit()
{
    // EA globals survive a re-initialization (input change, timeframe change), so reset them first
    errorLogged = false;

    // 1. Check DLL permissions
    if(!IsDllsAllowed())
    {
        Print("Initialization failed: Please check 'Allow DLL imports' in EA settings.");
        return(INIT_FAILED);
    }

    // 2. Validate inputs
    if(StringLen(InstanceID) == 0)
    {
        Print("Initialization failed: InstanceID is empty.");
        return(INIT_PARAMETERS_INCORRECT);
    }

    // A backslash is not allowed in a kernel object name. Leading/trailing whitespace and
    // invisible characters (often pasted by accident) would silently never match the config.json key.
    if(StringFind(InstanceID, "\\") >= 0 || HasInvisibleOrEdgeSpace(InstanceID))
    {
        Print("Initialization failed: InstanceID must not contain '\\', invisible characters, or leading/trailing spaces: '",
              InstanceID, "'");
        return(INIT_PARAMETERS_INCORRECT);
    }

    if(HeartbeatInterval <= 0)
    {
        Print("Initialization failed: HeartbeatInterval must be greater than 0.");
        return(INIT_PARAMETERS_INCORRECT);
    }

    // 3. Set Event Name (Local scope: same Windows session as Lifeguard)
    string eventName = "Local\\EA_Heartbeat_" + InstanceID;

    // 4. Duplicate check: only one EA per InstanceID may run in this Windows session.
    //    The name uses its own prefix (not EA_Heartbeat_) so it can never collide with another
    //    instance's heartbeat event (e.g. InstanceID "Owner_A" vs "A").
    //    Lifeguard never creates this mutex, so "already exists" can only mean another EA
    //    (another chart, or another MT4 terminal) already uses the same InstanceID.
    //    OnDeinit closes it, so re-initialization does not trip this check.
    //    OpenMutexW is used to test for existence (instead of CreateMutex + GetLastError),
    //    so the check does not depend on the last-error value surviving the MQL runtime.
    //    If the mutex exists but cannot be opened (access denied), CreateMutexW below fails too
    //    (it requests full access to an existing mutex), so the EA still refuses to start.
    string ownerName = "Local\\LifeguardOwner_" + InstanceID;
    int existing = OpenMutexW(0x00100000, 0, ownerName);   // SYNCHRONIZE
    if(existing != 0)
    {
        CloseHandle(existing);
        Print("Initialization failed: InstanceID '", InstanceID,
              "' is already used by another EA (another chart or another MT4 terminal). Each MT4 needs a unique InstanceID.");
        return(INIT_PARAMETERS_INCORRECT);
    }

    hOwnerMutex = CreateMutexW(0, 0, ownerName);
    if(hOwnerMutex == 0)
    {
        Print("Failed to create owner mutex. Windows System Error Code: ", kernel32::GetLastError());
        return(INIT_FAILED);
    }

    // 5. Create (or open, if Lifeguard already created it) the auto-reset event once.
    //    CreateEvent returns NULL (0) on failure, never INVALID_HANDLE_VALUE.
    hEventHandle = CreateEventW(0, 0, 0, eventName);
    if(hEventHandle == 0)
    {
        Print("Failed to create Event. Windows System Error Code: ", kernel32::GetLastError());
        CloseHandle(hOwnerMutex);
        hOwnerMutex = 0;
        return(INIT_FAILED);
    }

    // 6. Start Timer. If it fails there would be no heartbeat and Lifeguard would keep restarting MT4.
    if(!EventSetTimer(HeartbeatInterval))
    {
        Print("Initialization failed: EventSetTimer failed. MQL Error: ", _LastError);
        CloseHandle(hEventHandle);
        hEventHandle = 0;
        CloseHandle(hOwnerMutex);
        hOwnerMutex = 0;
        return(INIT_FAILED);
    }

    Print("Lifeguard Event registered successfully: ", eventName, " | Handle: ", hEventHandle);
    Print("HEARTBEAT: InstanceID='", InstanceID, "' -> Lifeguard config.json must have an MT4Instances key named exactly '",
          InstanceID, "' for this terminal.");

    return(INIT_SUCCEEDED);
}

// --- Deinitialization ---
void OnDeinit(const int reason)
{
    EventKillTimer();

    // Release Windows system resource when EA is removed or MT4 closes.
    // Heartbeats stop here: if the EA is removed while MT4 keeps running,
    // Lifeguard will treat MT4 as hung and restart it (expected).
    if(hEventHandle != 0)
    {
        CloseHandle(hEventHandle);
        hEventHandle = 0;
        Print("Watchdog Event handle closed and released safely.");
    }

    if(hOwnerMutex != 0)
    {
        CloseHandle(hOwnerMutex);
        hOwnerMutex = 0;
    }
}

// --- Timer Event ---
void OnTimer()
{
    // Signal the auto-reset event (SetEvent, not PulseEvent: PulseEvent is unreliable)
    if(hEventHandle != 0)
    {
        if(SetEvent(hEventHandle) == 0)
        {
            // Only print the error once to prevent log flooding
            if(!errorLogged)
            {
                Print("Failed to trigger SetEvent. Windows System Error Code: ", kernel32::GetLastError());
                errorLogged = true;
            }
        }
        else if(errorLogged)
        {
            // Log once when SetEvent works again, so the log shows it recovered (not that the EA stopped)
            errorLogged = false;
            Print("SetEvent recovered.");
        }
    }
}
