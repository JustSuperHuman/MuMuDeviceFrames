# AOSP platform test key

`platform.pk8` / `platform.x509.pem` are the **public** Android Open Source Project test keys, copied unchanged from
[`build/make/target/product/security`](https://android.googlesource.com/platform/build/+/refs/heads/main/target/product/security/)
(certificate SHA-256 `c8a2e9bc…192ab8`). Every AOSP-based emulator image that was never re-signed — MuMu Player among
them — is signed with these, which is why a resource overlay signed with them can override framework and SystemUI
resources without root.

They are test keys, published by Google for exactly this kind of use; there is nothing secret here.
