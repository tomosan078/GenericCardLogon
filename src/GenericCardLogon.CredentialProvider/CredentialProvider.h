#pragma once
#include <windows.h>
#include <credentialprovider.h>
#include <unknwn.h>
#include <string>
#include <vector>
#include <mutex>
#include <thread>
#include <atomic>

static const GUID CLSID_GenericCardLogonCredentialProvider =
{ 0x4fe441c9, 0x78d1, 0x4ca3, { 0x82, 0x17, 0xb7, 0x8c, 0xe7, 0xb7, 0x3e, 0xee } };

static const DWORD FIELD_PROVIDER_LABEL = 0;
static const DWORD FIELD_INSTRUCTION = 1;

class CredentialProvider final : public ICredentialProvider, public ICredentialProviderSetUserArray
{
public:
    CredentialProvider();
    ~CredentialProvider();
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;
    IFACEMETHODIMP SetUsageScenario(CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus, DWORD dwFlags) override;
    IFACEMETHODIMP SetSerialization(const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs) override;
    IFACEMETHODIMP Advise(ICredentialProviderEvents* pcpe, UINT_PTR upAdviseContext) override;
    IFACEMETHODIMP UnAdvise() override;
    IFACEMETHODIMP GetFieldDescriptorCount(DWORD* pdwCount) override;
    IFACEMETHODIMP GetFieldDescriptorAt(DWORD dwIndex, CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** ppcpfd) override;
    IFACEMETHODIMP GetCredentialCount(DWORD* pdwCount, DWORD* pdwDefault, BOOL* pbAutoLogonWithDefault) override;
    IFACEMETHODIMP GetCredentialAt(DWORD dwIndex, ICredentialProviderCredential** ppcpc) override;
    IFACEMETHODIMP SetUserArray(ICredentialProviderUserArray* users) override;

    void SetCardReady(const std::wstring& user, const std::vector<BYTE>& password);
    void SetGclCredentialSelected(const std::wstring& user, bool selected);
    bool TakeCardCredentials(std::wstring& user, std::vector<BYTE>& password);
    bool IsCardUser(const std::wstring& user) const;
    std::wstring GetCardUser() const;
    DWORD CredentialCount() const;
    std::wstring UserNameAt(DWORD index) const;
    std::wstring SidAt(DWORD index) const;
private:
    struct UserInfo
    {
        std::wstring userName;
        std::wstring sid;
    };

    LONG _ref;
    CREDENTIAL_PROVIDER_USAGE_SCENARIO _usageScenario;
    ICredentialProviderEvents* _events;
    UINT_PTR _adviseContext;
    mutable std::mutex _cacheMutex;
    std::vector<UserInfo> _users;
    std::wstring _cachedUser;
    std::vector<BYTE> _cachedPassword;
    bool _cardReady;
    bool _gclCredentialSelected;
    std::wstring _selectedUser;
};

class Credential final : public ICredentialProviderCredential2
{
public:
    explicit Credential(CredentialProvider* provider, bool unlock, const std::wstring& userName, const std::wstring& sid);
    ~Credential();
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;
    IFACEMETHODIMP Advise(ICredentialProviderCredentialEvents* pcpce) override;
    IFACEMETHODIMP UnAdvise() override;
    IFACEMETHODIMP SetSelected(BOOL* pbAutoLogon) override;
    IFACEMETHODIMP SetDeselected() override;
    IFACEMETHODIMP GetFieldState(DWORD dwFieldID, CREDENTIAL_PROVIDER_FIELD_STATE* pcpfs, CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* pcpfis) override;
    IFACEMETHODIMP GetStringValue(DWORD dwFieldID, PWSTR* ppwsz) override;
    IFACEMETHODIMP GetBitmapValue(DWORD dwFieldID, HBITMAP* phbmp) override;
    IFACEMETHODIMP GetCheckboxValue(DWORD dwFieldID, BOOL* pbChecked, PWSTR* ppwszLabel) override;
    IFACEMETHODIMP GetComboBoxValueCount(DWORD dwFieldID, DWORD* pcItems, DWORD* pdwSelectedItem) override;
    IFACEMETHODIMP GetComboBoxValueAt(DWORD dwFieldID, DWORD dwItem, PWSTR* ppwszItem) override;
    IFACEMETHODIMP GetSubmitButtonValue(DWORD dwFieldID, DWORD* pdwAdjacentTo) override;
    IFACEMETHODIMP SetStringValue(DWORD dwFieldID, PCWSTR pwz) override;
    IFACEMETHODIMP SetCheckboxValue(DWORD dwFieldID, BOOL bChecked) override;
    IFACEMETHODIMP SetComboBoxSelectedValue(DWORD dwFieldID, DWORD dwSelectedItem) override;
    IFACEMETHODIMP CommandLinkClicked(DWORD dwFieldID) override;
    IFACEMETHODIMP GetSerialization(CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* pcpgsr, CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs, PWSTR* ppszOptionalStatusText, CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon) override;
    IFACEMETHODIMP ReportResult(NTSTATUS ntsStatus, NTSTATUS ntsSubstatus, PWSTR* ppszOptionalStatusText, CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon) override;
    IFACEMETHODIMP GetUserSid(PWSTR* ppszSid) override;
private:
    LONG _ref;
    ICredentialProviderCredentialEvents* _events;
    std::atomic<bool> _selected;
    std::wstring _userName;
    std::wstring _sid;
    std::wstring _cachedUser;
    std::vector<BYTE> _cachedPassword;
    std::wstring _lastError;
    bool _unlock;
    CredentialProvider* _provider;
    std::atomic<bool> _stopWorker;
    std::atomic<bool> _workerRunning;
    std::thread _worker;
    void StartCardWorker();
    void StopCardWorker();
};

class ClassFactory final : public IClassFactory
{
public:
    ClassFactory();
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    IFACEMETHODIMP_(ULONG) AddRef() override;
    IFACEMETHODIMP_(ULONG) Release() override;
    IFACEMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv) override;
    IFACEMETHODIMP LockServer(BOOL fLock) override;
private:
    LONG _ref;
};
