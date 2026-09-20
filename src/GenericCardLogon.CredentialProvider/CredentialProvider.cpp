#include "CredentialProvider.h"
#include <windows.h>
#include <credentialprovider.h>
#include <ntsecapi.h>
#include <strsafe.h>
#include <new>
#include <string>
#include <vector>
#include <cstring>
#include <algorithm>
#include <cwctype>
#include "resource.h"

#pragma comment(lib, "Ole32.lib")
#pragma comment(lib, "Advapi32.lib")
#pragma comment(lib, "Secur32.lib")

static HMODULE g_module = nullptr;
static volatile LONG g_objects = 0;
static volatile LONG g_locks = 0;
static const wchar_t* kPipeName = L"\\\\.\\pipe\\GenericCardLogon.Logon.v1";
static const GUID kLabelGuid = {0x286bbff3,0xbad4,0x438f,{0xb0,0x07,0x79,0xb7,0x26,0x7c,0x3d,0x48}};

// These constants are defined by the Windows 8+ Credential Provider contract,
// but some Windows SDK installations do not expose the symbolic names in
// credentialprovider.h. Keep the GUIDs local so the project builds against
// older/newer SDKs alike.
static const GUID kLogoGuid = {0x2d837775,0xf6cd,0x464e,{0xa7,0x45,0x48,0x2f,0xd0,0xb4,0x74,0x93}};
static const PROPERTYKEY kIdentityQualifiedUserName =
{ {0xda520e51,0xf4e9,0x4739,{0xac,0x82,0x02,0xe0,0xa9,0x5c,0x90,0x30}}, 100 };
static const PROPERTYKEY kIdentityUserName =
{ {0xc4322503,0x78ca,0x49c6,{0x9a,0xcc,0xa6,0x8e,0x2a,0xfd,0x7b,0x6b}}, 100 };

static PWSTR DupString(PCWSTR value)
{
    if (!value) return nullptr;
    const SIZE_T bytes = (wcslen(value) + 1) * sizeof(wchar_t);
    PWSTR p = static_cast<PWSTR>(CoTaskMemAlloc(bytes));
    if (p) memcpy(p, value, bytes);
    return p;
}

static HRESULT CopyFieldDescriptor(const CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR& src, CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** dst)
{
    if (!dst) return E_POINTER;
    *dst = nullptr;
    CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR* p = static_cast<CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR*>(CoTaskMemAlloc(sizeof(CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR)));
    if (!p) return E_OUTOFMEMORY;
    *p = src;
    p->pszLabel = DupString(src.pszLabel);
    if (!p->pszLabel) { CoTaskMemFree(p); return E_OUTOFMEMORY; }
    *dst = p;
    return S_OK;
}

static const DWORD FIELD_PROVIDER_LOGO = 2;
static CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR g_fields[] =
{
    { FIELD_PROVIDER_LABEL, CPFT_SMALL_TEXT, const_cast<PWSTR>(L"GenericCardLogon"), kLabelGuid },
    { FIELD_INSTRUCTION, CPFT_LARGE_TEXT, const_cast<PWSTR>(L"ICカードをかざしてください"), GUID_NULL },
    { FIELD_PROVIDER_LOGO, CPFT_TILE_IMAGE, const_cast<PWSTR>(L"GenericCardLogon"), kLogoGuid }
};
static const DWORD kFieldCount = sizeof(g_fields) / sizeof(g_fields[0]);

static bool ReadExact(HANDLE h, BYTE* p, DWORD n)
{
    while (n != 0)
    {
        DWORD got = 0;
        if (!ReadFile(h, p, n, &got, nullptr) || got == 0) return false;
        p += got; n -= got;
    }
    return true;
}

static bool WriteAll(HANDLE h, const BYTE* p, DWORD n)
{
    while (n != 0)
    {
        DWORD sent = 0;
        if (!WriteFile(h, p, n, &sent, nullptr) || sent == 0) return false;
        p += sent; n -= sent;
    }
    return true;
}

static std::wstring Utf8ToWide(const BYTE* data, DWORD length)
{
    if (!data || length == 0) return std::wstring();
    int n = MultiByteToWideChar(CP_UTF8, 0, reinterpret_cast<LPCCH>(data), static_cast<int>(length), nullptr, 0);
    if (n <= 0) return std::wstring();
    std::wstring result(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, reinterpret_cast<LPCCH>(data), static_cast<int>(length), &result[0], n);
    return result;
}


static std::wstring UserOnly(const std::wstring& value)
{
    const size_t slash = value.find_last_of(L'\\');
    if (slash != std::wstring::npos && slash + 1 < value.size()) return value.substr(slash + 1);
    return value;
}

static bool AccountMatches(const std::wstring& a, const std::wstring& b)
{
    if (a.empty() || b.empty()) return false;
    auto lower = [](std::wstring s)
    {
        std::transform(s.begin(), s.end(), s.begin(), [](wchar_t c) { return static_cast<wchar_t>(towlower(c)); });
        return s;
    };
    const std::wstring al = lower(a);
    const std::wstring bl = lower(b);
    if (al == bl) return true;
    return lower(UserOnly(al)) == lower(UserOnly(bl));
}

static HBITMAP CreateProviderLogo()
{
    HICON icon = static_cast<HICON>(LoadImageW(g_module, MAKEINTRESOURCEW(IDI_GENERICCARDLOGON),
        IMAGE_ICON, 72, 72, LR_DEFAULTCOLOR));
    if (!icon) return nullptr;

    HDC screen = GetDC(nullptr);
    HDC dc = CreateCompatibleDC(screen);
    HBITMAP bmp = CreateCompatibleBitmap(screen, 72, 72);
    HGDIOBJ old = SelectObject(dc, bmp);
    RECT rc = { 0, 0, 72, 72 };
    FillRect(dc, &rc, static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH)));
    DrawIconEx(dc, 0, 0, icon, 72, 72, 0, nullptr, DI_NORMAL);
    SelectObject(dc, old);
    DeleteDC(dc);
    ReleaseDC(nullptr, screen);
    DestroyIcon(icon);
    return bmp;
}

static bool QueryService(std::wstring& user, std::vector<BYTE>& password, std::wstring& error)
{
    user.clear(); password.clear(); error.clear();
    HANDLE h = CreateFileW(kPipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
    if (h == INVALID_HANDLE_VALUE && GetLastError() == ERROR_PIPE_BUSY)
    {
        if (!WaitNamedPipeW(kPipeName, 10000)) { error = L"GenericCardLogon Serviceに接続できません。"; return false; }
        h = CreateFileW(kPipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
    }
    if (h == INVALID_HANDLE_VALUE) { error = L"GenericCardLogon Serviceに接続できません。"; return false; }

    BYTE req[4] = { 'A','U','T','H' };
    bool ok = WriteAll(h, req, 4);
    BYTE status = 1;
    BYTE lens[4] = {};
    if (ok) ok = ReadExact(h, &status, 1);
    if (ok && status == 0)
    {
        ok = ReadExact(h, lens, 4);
        const DWORD userLen = static_cast<DWORD>(lens[0] | (lens[1] << 8));
        const DWORD passLen = static_cast<DWORD>(lens[2] | (lens[3] << 8));
        if (ok && userLen <= 32767 && passLen <= 65534)
        {
            std::vector<BYTE> ub(userLen), pb(passLen);
            if (userLen != 0) ok = ReadExact(h, ub.data(), userLen);
            if (ok && passLen != 0) ok = ReadExact(h, pb.data(), passLen);
            if (ok) { user = Utf8ToWide(ub.data(), userLen); password.swap(pb); }
        }
        else ok = false;
    }
    else if (ok)
    {
        ok = ReadExact(h, lens, 2);
        const DWORD msgLen = static_cast<DWORD>(lens[0] | (lens[1] << 8));
        std::vector<BYTE> mb(msgLen);
        if (ok && msgLen != 0) ok = ReadExact(h, mb.data(), msgLen);
        if (ok) error = Utf8ToWide(mb.data(), msgLen);
    }
    CloseHandle(h);
    if (!ok) { password.clear(); error = L"GenericCardLogon Serviceから不正な応答を受信しました。"; return false; }
    return status == 0;
}

static bool LookupNegotiatePackage(ULONG& packageId)
{
    HANDLE handle = nullptr;
    if (LsaConnectUntrusted(&handle) != 0) return false;
    char name[] = "Negotiate";
    LSA_STRING lsaName = {};
    lsaName.Buffer = name;
    lsaName.Length = static_cast<USHORT>(strlen(name));
    lsaName.MaximumLength = lsaName.Length;
    NTSTATUS status = LsaLookupAuthenticationPackage(handle, &lsaName, &packageId);
    LsaDeregisterLogonProcess(handle);
    return status == 0;
}

#pragma pack(push, 8)
struct SERIAL_UNICODE_STRING
{
    USHORT Length;
    USHORT MaximumLength;
    ULONGLONG Buffer;
};
struct SERIAL_KERB_INTERACTIVE_LOGON
{
    ULONG MessageType;
    SERIAL_UNICODE_STRING LogonDomainName;
    SERIAL_UNICODE_STRING UserName;
    SERIAL_UNICODE_STRING Password;
};
struct SERIAL_KERB_INTERACTIVE_UNLOCK_LOGON
{
    SERIAL_KERB_INTERACTIVE_LOGON Logon;
    LUID LogonId;
};
#pragma pack(pop)

static bool BuildKerberosSerialization(const std::wstring& account, const std::vector<BYTE>& password, bool unlock, CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcs)
{
    if (!pcs || account.empty() || password.size() > 65534) return false;

    std::wstring domain;
    std::wstring user = account;
    const size_t slash = account.find(L'\\');
    if (slash != std::wstring::npos) { domain = account.substr(0, slash); user = account.substr(slash + 1); }
    else domain = L".";
    if (user.empty()) return false;

    const DWORD domainBytes = static_cast<DWORD>(domain.size() * sizeof(wchar_t));
    const DWORD userBytes = static_cast<DWORD>(user.size() * sizeof(wchar_t));
    const DWORD header = static_cast<DWORD>(sizeof(SERIAL_KERB_INTERACTIVE_UNLOCK_LOGON));
    const DWORD total = header + domainBytes + 2 + userBytes + 2 + static_cast<DWORD>(password.size()) + 2;
    BYTE* buffer = static_cast<BYTE*>(CoTaskMemAlloc(total));
    if (!buffer) return false;
    ZeroMemory(buffer, total);

    SERIAL_KERB_INTERACTIVE_UNLOCK_LOGON* logon = reinterpret_cast<SERIAL_KERB_INTERACTIVE_UNLOCK_LOGON*>(buffer);
    DWORD offset = header;

    memcpy(buffer + offset, domain.data(), domainBytes);
    logon->Logon.LogonDomainName.Length = static_cast<USHORT>(domainBytes);
    logon->Logon.LogonDomainName.MaximumLength = static_cast<USHORT>(domainBytes + 2);
    logon->Logon.LogonDomainName.Buffer = offset;
    offset += domainBytes + 2;

    memcpy(buffer + offset, user.data(), userBytes);
    logon->Logon.UserName.Length = static_cast<USHORT>(userBytes);
    logon->Logon.UserName.MaximumLength = static_cast<USHORT>(userBytes + 2);
    logon->Logon.UserName.Buffer = offset;
    offset += userBytes + 2;

    if (!password.empty()) memcpy(buffer + offset, password.data(), password.size());
    logon->Logon.Password.Length = static_cast<USHORT>(password.size());
    logon->Logon.Password.MaximumLength = static_cast<USHORT>(password.size() + 2);
    logon->Logon.Password.Buffer = offset;
    logon->Logon.MessageType = unlock ? 7 : 2;

    ULONG packageId = 0;
    if (!LookupNegotiatePackage(packageId))
    {
        SecureZeroMemory(buffer, total); CoTaskMemFree(buffer); return false;
    }
    pcs->ulAuthenticationPackage = packageId;
    pcs->clsidCredentialProvider = CLSID_GenericCardLogonCredentialProvider;
    pcs->cbSerialization = total;
    pcs->rgbSerialization = buffer;
    return true;
}

CredentialProvider::CredentialProvider() : _ref(1), _usageScenario(CPUS_LOGON), _events(nullptr), _adviseContext(0), _cardReady(false), _gclCredentialSelected(false)
{
    InterlockedIncrement(&g_objects);
}
CredentialProvider::~CredentialProvider()
{
    std::lock_guard<std::mutex> lock(_cacheMutex);
    if (_events) _events->Release();
    SecureZeroMemory(_cachedPassword.data(), _cachedPassword.size());
    _cachedPassword.clear();
    _users.clear();
    InterlockedDecrement(&g_objects);
}
IFACEMETHODIMP CredentialProvider::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (riid == IID_IUnknown || riid == IID_ICredentialProvider)
        *ppv = static_cast<ICredentialProvider*>(this);
    else if (riid == IID_ICredentialProviderSetUserArray)
        *ppv = static_cast<ICredentialProviderSetUserArray*>(this);
    else
        return E_NOINTERFACE;
    AddRef();
    return S_OK;
}
IFACEMETHODIMP_(ULONG) CredentialProvider::AddRef() { return InterlockedIncrement(&_ref); }
IFACEMETHODIMP_(ULONG) CredentialProvider::Release() { ULONG n = InterlockedDecrement(&_ref); if (!n) delete this; return n; }

IFACEMETHODIMP CredentialProvider::SetUsageScenario(CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus, DWORD)
{
    if (cpus != CPUS_LOGON && cpus != CPUS_UNLOCK_WORKSTATION && cpus != CPUS_CREDUI) return E_NOTIMPL;
    _usageScenario = cpus;
    return S_OK;
}
IFACEMETHODIMP CredentialProvider::SetSerialization(const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION*) { return E_NOTIMPL; }
IFACEMETHODIMP CredentialProvider::Advise(ICredentialProviderEvents* events, UINT_PTR context)
{
    std::lock_guard<std::mutex> lock(_cacheMutex);
    if (_events) _events->Release();
    _events = events;
    if (_events) _events->AddRef();
    _adviseContext = context;
    return S_OK;
}
IFACEMETHODIMP CredentialProvider::UnAdvise()
{
    std::lock_guard<std::mutex> lock(_cacheMutex);
    if (_events) _events->Release();
    _events = nullptr;
    _adviseContext = 0;
    return S_OK;
}
IFACEMETHODIMP CredentialProvider::SetUserArray(ICredentialProviderUserArray* users)
{
    std::lock_guard<std::mutex> lock(_cacheMutex);
    _users.clear();
    if (!users) return E_POINTER;

    DWORD count = 0;
    HRESULT hr = users->GetCount(&count);
    if (FAILED(hr)) return hr;

    for (DWORD i = 0; i < count; ++i)
    {
        ICredentialProviderUser* user = nullptr;
        if (FAILED(users->GetAt(i, &user)) || !user) continue;

        PWSTR sid = nullptr;
        PWSTR name = nullptr;
        HRESULT hs = user->GetSid(&sid);
        HRESULT hn = user->GetStringValue(kIdentityQualifiedUserName, &name);
        if (FAILED(hn) || !name)
            hn = user->GetStringValue(kIdentityUserName, &name);

        if (SUCCEEDED(hs) && sid && *sid && SUCCEEDED(hn) && name && *name)
            _users.push_back({ name, sid });

        if (sid) CoTaskMemFree(sid);
        if (name) CoTaskMemFree(name);
        user->Release();
    }
    return S_OK;
}
IFACEMETHODIMP CredentialProvider::GetFieldDescriptorCount(DWORD* count)
{
    if (!count) return E_POINTER;
    *count = kFieldCount;
    return S_OK;
}
IFACEMETHODIMP CredentialProvider::GetFieldDescriptorAt(DWORD index, CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** out)
{
    if (index >= kFieldCount) return E_INVALIDARG;
    return CopyFieldDescriptor(g_fields[index], out);
}
IFACEMETHODIMP CredentialProvider::GetCredentialCount(DWORD* count, DWORD* def, BOOL* autoLogon)
{
    if (!count || !def || !autoLogon) return E_POINTER;
    std::lock_guard<std::mutex> lock(_cacheMutex);

    *def = CREDENTIAL_PROVIDER_NO_DEFAULT;
    *autoLogon = FALSE;

    if ((_usageScenario == CPUS_LOGON || _usageScenario == CPUS_UNLOCK_WORKSTATION) && !_users.empty())
    {
        *count = static_cast<DWORD>(_users.size());

        // Only advertise auto-logon when the user explicitly selected a GCL
        // credential AND a card has actually been detected for that same user.
        // This is important for coexistence with Windows Password/PIN: before
        // card detection GCL never asks LogonUI to auto-submit anything.
        if (_gclCredentialSelected && _cardReady && !_selectedUser.empty() &&
            AccountMatches(_selectedUser, _cachedUser))
        {
            for (DWORD i = 0; i < _users.size(); ++i)
            {
                if (AccountMatches(_users[i].userName, _selectedUser))
                {
                    *def = i;
                    *autoLogon = TRUE;
                    break;
                }
            }
        }
        return S_OK;
    }

    // Credential UI / compatibility path.  Never advertise automatic logon
    // without a user array because there is no safe user-to-SID association.
    *count = 1;
    return S_OK;
}
IFACEMETHODIMP CredentialProvider::GetCredentialAt(DWORD index, ICredentialProviderCredential** out)
{
    if (!out) return E_POINTER;
    *out = nullptr;

    std::wstring userName;
    std::wstring sid;
    {
        std::lock_guard<std::mutex> lock(_cacheMutex);
        if ((_usageScenario == CPUS_LOGON || _usageScenario == CPUS_UNLOCK_WORKSTATION) && !_users.empty())
        {
            if (index >= _users.size()) return E_INVALIDARG;
            userName = _users[index].userName;
            sid = _users[index].sid;
        }
        else
        {
            if (index != 0) return E_INVALIDARG;
        }
    }

    Credential* c = new(std::nothrow) Credential(this, _usageScenario == CPUS_UNLOCK_WORKSTATION, userName, sid);
    if (!c) return E_OUTOFMEMORY;
    *out = static_cast<ICredentialProviderCredential*>(c);
    return S_OK;
}
void CredentialProvider::SetCardReady(const std::wstring& user, const std::vector<BYTE>& password)
{
    ICredentialProviderEvents* events = nullptr;
    UINT_PTR context = 0;
    {
        std::lock_guard<std::mutex> lock(_cacheMutex);
        SecureZeroMemory(_cachedPassword.data(), _cachedPassword.size());
        _cachedPassword = password;
        _cachedUser = user;
        _cardReady = !_cachedUser.empty() && !_cachedPassword.empty();
        if (_events && _cardReady && _gclCredentialSelected)
        {
            events = _events;
            events->AddRef();
            context = _adviseContext;
        }
    }
    if (events)
    {
        events->CredentialsChanged(context);
        events->Release();
    }
}
void CredentialProvider::SetGclCredentialSelected(const std::wstring& user, bool selected)
{
    std::lock_guard<std::mutex> lock(_cacheMutex);
    _gclCredentialSelected = selected;
    if (selected)
    {
        // A newly selected GCL tile must never inherit credentials detected
        // while another tile/user was selected.
        if (!AccountMatches(_selectedUser, user))
        {
            SecureZeroMemory(_cachedPassword.data(), _cachedPassword.size());
            _cachedPassword.clear();
            _cachedUser.clear();
            _cardReady = false;
        }
        _selectedUser = user;
    }
    else
    {
        // Switching to Password/PIN must immediately invalidate any cached
        // card credentials.  This is deliberately done without waiting for
        // the reader worker, so the standard Windows provider remains usable.
        _selectedUser.clear();
        SecureZeroMemory(_cachedPassword.data(), _cachedPassword.size());
        _cachedPassword.clear();
        _cachedUser.clear();
        _cardReady = false;
    }
}

bool CredentialProvider::TakeCardCredentials(std::wstring& user, std::vector<BYTE>& password)
{
    std::lock_guard<std::mutex> lock(_cacheMutex);
    if (!_cardReady || !_gclCredentialSelected || _selectedUser.empty()) return false;
    if (!AccountMatches(_selectedUser, _cachedUser)) return false;
    user = _cachedUser;
    password = _cachedPassword;
    SecureZeroMemory(_cachedPassword.data(), _cachedPassword.size());
    _cachedPassword.clear();
    _cachedUser.clear();
    _cardReady = false;
    return true;
}
bool CredentialProvider::IsCardUser(const std::wstring& user) const
{
    std::lock_guard<std::mutex> lock(_cacheMutex);
    return _cardReady && AccountMatches(_cachedUser, user);
}
std::wstring CredentialProvider::GetCardUser() const
{
    std::lock_guard<std::mutex> lock(_cacheMutex);
    return _cachedUser;
}
DWORD CredentialProvider::CredentialCount() const
{
    std::lock_guard<std::mutex> lock(_cacheMutex);
    return static_cast<DWORD>(_users.size());
}
std::wstring CredentialProvider::UserNameAt(DWORD index) const
{
    std::lock_guard<std::mutex> lock(_cacheMutex);
    return index < _users.size() ? _users[index].userName : std::wstring();
}
std::wstring CredentialProvider::SidAt(DWORD index) const
{
    std::lock_guard<std::mutex> lock(_cacheMutex);
    return index < _users.size() ? _users[index].sid : std::wstring();
}

Credential::Credential(CredentialProvider* provider, bool unlock, const std::wstring& userName, const std::wstring& sid) : _ref(1), _provider(provider), _events(nullptr), _selected(false), _userName(userName), _sid(sid), _unlock(unlock), _stopWorker(false), _workerRunning(false)
{
    if (_provider) _provider->AddRef();
    InterlockedIncrement(&g_objects);
}
Credential::~Credential()
{
    StopCardWorker();
    if (_events) _events->Release();
    if (_provider) _provider->Release();
    SecureZeroMemory(_cachedPassword.data(), _cachedPassword.size());
    _cachedPassword.clear();
    InterlockedDecrement(&g_objects);
}
IFACEMETHODIMP Credential::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (riid == IID_IUnknown || riid == IID_ICredentialProviderCredential)
        *ppv = static_cast<ICredentialProviderCredential*>(this);
    else if (riid == IID_ICredentialProviderCredential2)
        *ppv = static_cast<ICredentialProviderCredential2*>(this);
    else
        return E_NOINTERFACE;
    AddRef();
    return S_OK;
}
IFACEMETHODIMP_(ULONG) Credential::AddRef() { return InterlockedIncrement(&_ref); }
IFACEMETHODIMP_(ULONG) Credential::Release() { ULONG n = InterlockedDecrement(&_ref); if (!n) delete this; return n; }
IFACEMETHODIMP Credential::Advise(ICredentialProviderCredentialEvents* e) { if (_events) _events->Release(); _events = e; if (_events) _events->AddRef(); return S_OK; }
IFACEMETHODIMP Credential::UnAdvise() { if (_events) _events->Release(); _events = nullptr; return S_OK; }
void Credential::StartCardWorker()
{
    // Do not let a deselected GCL credential block LogonUI.  QueryService can
    // wait for several seconds while the service polls the reader.  The worker
    // therefore owns no extra COM reference and is joined only when the
    // credential itself is destroyed.
    if (_worker.joinable())
    {
        if (_workerRunning.load()) return;
        _worker.join();
    }

    _stopWorker.store(false);
    _workerRunning.store(true);
    _worker = std::thread([this]()
    {
        while (!_stopWorker.load())
        {
            std::wstring user, error;
            std::vector<BYTE> password;
            if (QueryService(user, password, error))
            {
                if (!_stopWorker.load() && _selected && _provider)
                    _provider->SetCardReady(user, password);
                SecureZeroMemory(password.data(), password.size());
                break;
            }
            SecureZeroMemory(password.data(), password.size());
            for (int i = 0; i < 5 && !_stopWorker.load(); ++i)
                Sleep(100);
        }
        _workerRunning.store(false);
    });
}
void Credential::StopCardWorker()
{
    _stopWorker.store(true);
    // Intentionally do not join here when called from SetDeselected().
    // QueryService is a synchronous named-pipe call and the Service may still
    // be polling the reader.  Joining here would block the LogonUI thread and
    // can make the Windows Password/PIN provider appear unresponsive.
}
IFACEMETHODIMP Credential::SetSelected(BOOL* autoLogon)
{
    if (!autoLogon) return E_POINTER;
    *autoLogon = FALSE;
    _selected = true;
    _cachedUser.clear();
    SecureZeroMemory(_cachedPassword.data(), _cachedPassword.size());
    _cachedPassword.clear();
    _lastError.clear();
    if (_provider)
    {
        _provider->SetGclCredentialSelected(_userName, true);
        // After a CredentialsChanged refresh, LogonUI may select the newly
        // created credential again.  If the card result survived that refresh
        // and belongs to this user, allow LogonUI to submit this credential.
        *autoLogon = _provider->IsCardUser(_userName) ? TRUE : FALSE;
    }
    StartCardWorker();
    return S_OK;
}
IFACEMETHODIMP Credential::SetDeselected()
{
    _selected = false;
    StopCardWorker();
    if (_provider) _provider->SetGclCredentialSelected(_userName, false);
    SecureZeroMemory(_cachedPassword.data(), _cachedPassword.size());
    _cachedPassword.clear();
    _cachedUser.clear();
    return S_OK;
}
IFACEMETHODIMP Credential::GetFieldState(DWORD id, CREDENTIAL_PROVIDER_FIELD_STATE* state, CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* interactive) { if (!state || !interactive || id >= kFieldCount) return E_INVALIDARG; *state = CPFS_DISPLAY_IN_BOTH; *interactive = CPFIS_NONE; return S_OK; }

static std::wstring ReadUiString(const wchar_t* valueName, const wchar_t* fallback)
{
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\GenericCardLogon", 0, KEY_READ, &key) != ERROR_SUCCESS)
        return fallback;
    wchar_t buffer[512] = {};
    DWORD type = 0, size = sizeof(buffer);
    LONG r = RegQueryValueExW(key, valueName, nullptr, &type, reinterpret_cast<BYTE*>(buffer), &size);
    RegCloseKey(key);
    if (r != ERROR_SUCCESS || type != REG_SZ || buffer[0] == L'\0') return fallback;
    buffer[ARRAYSIZE(buffer) - 1] = L'\0';
    return buffer;
}
IFACEMETHODIMP Credential::GetStringValue(DWORD id, PWSTR* out)
{
    if (!out || id >= kFieldCount) return E_INVALIDARG;
    if (id == FIELD_PROVIDER_LOGO) return E_INVALIDARG;
    const std::wstring value = id == FIELD_INSTRUCTION
        ? ReadUiString(L"Instruction", L"ICカードをかざしてください")
        : ReadUiString(L"ProviderLabel", L"GenericCardLogon");
    *out = DupString(value.c_str());
    return *out ? S_OK : E_OUTOFMEMORY;
}
IFACEMETHODIMP Credential::GetBitmapValue(DWORD id, HBITMAP* bitmap)
{
    if (!bitmap) return E_POINTER;
    *bitmap = nullptr;
    if (id != FIELD_PROVIDER_LOGO) return E_INVALIDARG;
    *bitmap = CreateProviderLogo();
    return *bitmap ? S_OK : HRESULT_FROM_WIN32(GetLastError());
}
IFACEMETHODIMP Credential::GetCheckboxValue(DWORD, BOOL*, PWSTR*) { return E_NOTIMPL; }
IFACEMETHODIMP Credential::GetComboBoxValueCount(DWORD, DWORD*, DWORD*) { return E_NOTIMPL; }
IFACEMETHODIMP Credential::GetComboBoxValueAt(DWORD, DWORD, PWSTR*) { return E_NOTIMPL; }
IFACEMETHODIMP Credential::GetSubmitButtonValue(DWORD, DWORD*) { return E_NOTIMPL; }
IFACEMETHODIMP Credential::SetStringValue(DWORD, PCWSTR) { return E_NOTIMPL; }
IFACEMETHODIMP Credential::SetCheckboxValue(DWORD, BOOL) { return E_NOTIMPL; }
IFACEMETHODIMP Credential::SetComboBoxSelectedValue(DWORD, DWORD) { return E_NOTIMPL; }
IFACEMETHODIMP Credential::CommandLinkClicked(DWORD) { return E_NOTIMPL; }

IFACEMETHODIMP Credential::GetSerialization(CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* response, CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcs, PWSTR* status, CREDENTIAL_PROVIDER_STATUS_ICON* icon)
{
    if (!response || !pcs || !status || !icon) return E_POINTER;
    ZeroMemory(pcs, sizeof(*pcs)); *status = nullptr; *icon = CPSI_NONE; *response = CPGSR_NO_CREDENTIAL_NOT_FINISHED;

    std::wstring user, error;
    std::vector<BYTE> password;
    if (_provider && _provider->TakeCardCredentials(user, password))
    {
        // Use the card-detected credentials supplied by the provider.
    }
    else if (!_cachedUser.empty() && !_cachedPassword.empty())
    {
        user = _cachedUser;
        password = _cachedPassword;
    }
    else if (!QueryService(user, password, error))
    {
        *status = DupString(error.empty() ? L"ICカード認証に失敗しました。" : error.c_str());
        return S_OK;
    }
    if (!_userName.empty() && !_sid.empty() && !AccountMatches(_userName, user))
    {
        SecureZeroMemory(password.data(), password.size());
        *status = DupString(L"このユーザーには登録されていないカードです。");
        return S_OK;
    }
    const bool built = BuildKerberosSerialization(user, password, _unlock, pcs);
    SecureZeroMemory(password.data(), password.size());
    SecureZeroMemory(_cachedPassword.data(), _cachedPassword.size());
    _cachedPassword.clear();
    _cachedUser.clear();
    if (!built)
    {
        *status = DupString(L"Windowsログオン情報の作成に失敗しました。");
        return S_OK;
    }
    *response = CPGSR_RETURN_CREDENTIAL_FINISHED;
    return S_OK;
}
IFACEMETHODIMP Credential::GetUserSid(PWSTR* outSid)
{
    if (!outSid) return E_POINTER;
    *outSid = nullptr;
    if (_sid.empty()) return E_UNEXPECTED;
    *outSid = DupString(_sid.c_str());
    return *outSid ? S_OK : E_OUTOFMEMORY;
}

IFACEMETHODIMP Credential::ReportResult(NTSTATUS status, NTSTATUS substatus, PWSTR* text, CREDENTIAL_PROVIDER_STATUS_ICON* icon)
{
    UNREFERENCED_PARAMETER(substatus); UNREFERENCED_PARAMETER(status);
    if (text) *text = nullptr; if (icon) *icon = CPSI_NONE; return S_OK;
}

ClassFactory::ClassFactory() : _ref(1) { InterlockedIncrement(&g_objects); }
IFACEMETHODIMP ClassFactory::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER; *ppv = nullptr;
    if (riid == IID_IUnknown || riid == IID_IClassFactory) { *ppv = static_cast<IClassFactory*>(this); AddRef(); return S_OK; }
    return E_NOINTERFACE;
}
IFACEMETHODIMP_(ULONG) ClassFactory::AddRef() { return InterlockedIncrement(&_ref); }
IFACEMETHODIMP_(ULONG) ClassFactory::Release() { ULONG n = InterlockedDecrement(&_ref); if (!n) { InterlockedDecrement(&g_objects); delete this; } return n; }
IFACEMETHODIMP ClassFactory::CreateInstance(IUnknown* outer, REFIID riid, void** ppv) { if (outer) return CLASS_E_NOAGGREGATION; if (!ppv) return E_POINTER; *ppv = nullptr; CredentialProvider* p = new(std::nothrow) CredentialProvider(); if (!p) return E_OUTOFMEMORY; HRESULT hr = p->QueryInterface(riid, ppv); p->Release(); return hr; }
IFACEMETHODIMP ClassFactory::LockServer(BOOL lock) { if (lock) InterlockedIncrement(&g_locks); else InterlockedDecrement(&g_locks); return S_OK; }

extern "C" STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    if (rclsid != CLSID_GenericCardLogonCredentialProvider) return CLASS_E_CLASSNOTAVAILABLE;
    ClassFactory* f = new(std::nothrow) ClassFactory(); if (!f) return E_OUTOFMEMORY;
    HRESULT hr = f->QueryInterface(riid, ppv); f->Release(); return hr;
}
extern "C" STDAPI DllCanUnloadNow(void) { return (g_objects == 0 && g_locks == 0) ? S_OK : S_FALSE; }

static HRESULT WriteRegString(HKEY root, const wchar_t* path, const wchar_t* name, const wchar_t* value)
{
    HKEY key = nullptr; LONG r = RegCreateKeyExW(root, path, 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr);
    if (r != ERROR_SUCCESS) return HRESULT_FROM_WIN32(r);
    r = RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value), static_cast<DWORD>((wcslen(value) + 1) * sizeof(wchar_t)));
    RegCloseKey(key); return HRESULT_FROM_WIN32(r);
}
extern "C" STDAPI DllRegisterServer(void)
{
    wchar_t module[MAX_PATH] = {}; if (!GetModuleFileNameW(g_module, module, ARRAYSIZE(module))) return HRESULT_FROM_WIN32(GetLastError());
    wchar_t clsid[64] = {}; StringFromGUID2(CLSID_GenericCardLogonCredentialProvider, clsid, ARRAYSIZE(clsid));
    wchar_t cp[256] = {}; StringCchPrintfW(cp, ARRAYSIZE(cp), L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Authentication\\Credential Providers\\%s", clsid);
    HRESULT hr = WriteRegString(HKEY_LOCAL_MACHINE, cp, nullptr, L"GenericCardLogon"); if (FAILED(hr)) return hr;
    wchar_t inproc[256] = {}; StringCchPrintfW(inproc, ARRAYSIZE(inproc), L"SOFTWARE\\Classes\\CLSID\\%s\\InprocServer32", clsid);
    hr = WriteRegString(HKEY_LOCAL_MACHINE, inproc, nullptr, module); if (FAILED(hr)) return hr;
    return WriteRegString(HKEY_LOCAL_MACHINE, inproc, L"ThreadingModel", L"Both");
}
extern "C" STDAPI DllUnregisterServer(void)
{
    wchar_t clsid[64] = {}; StringFromGUID2(CLSID_GenericCardLogonCredentialProvider, clsid, ARRAYSIZE(clsid));
    wchar_t cp[256] = {}; StringCchPrintfW(cp, ARRAYSIZE(cp), L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Authentication\\Credential Providers\\%s", clsid);
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, cp);
    wchar_t path[256] = {}; StringCchPrintfW(path, ARRAYSIZE(path), L"SOFTWARE\\Classes\\CLSID\\%s", clsid);
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, path); return S_OK;
}
BOOL APIENTRY DllMain(HMODULE h, DWORD reason, LPVOID) { if (reason == DLL_PROCESS_ATTACH) { g_module = h; DisableThreadLibraryCalls(h); } return TRUE; }
