# GenericCardLogon.Service

LocalSystem service used by the Credential Provider.

Flow:

Credential Provider -> named pipe -> Service -> RC-S380/FeliCa polling -> IDm hash -> registration lookup -> DPAPI credential recovery.

The service does not register or load a custom LSA Authentication Package.
