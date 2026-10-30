# Security policy

## Reporting a vulnerability

Please report security problems privately by e-mail to **security@rintup.com**. Do not open a public
GitHub issue for them.

Include what you found, how to reproduce it and the agent version (`rintup-agent --version`).
We confirm receipt and keep you updated while we work on a fix.

## Supported versions

Fixes are released as a new agent version. Re-run the install script (it upgrades in place) or pull the
newest `ghcr.io/rintuphq/agent` image to receive them.

## Verifying releases

Every release binary has a `.sha256` file and a Sigstore bundle (`.sigstore.json`) produced by cosign
keyless signing in this repository's release workflow; the container image is signed the same way.
See "Verifying downloads" in the [README](README.md).
