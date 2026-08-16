# DCC by NV-UV 0.1.0-alpha.12

Experimental binary-only Community Preview for NVIDIA GeForce GPUs.

## Compatibility hotfix

- Fixed `Invalid LIMIT command` when Windows or a protected game process did
  not expose the full executable path to the DCC user interface.
- Empty optional game fields are now transported explicitly instead of being
  lost while the elevated helper parses the command.
- Missing executable or display names are derived from the remaining trusted
  game metadata where possible.
- Malformed commands are rejected before NVIDIA profile state is changed.
- Helper rejections now produce a clearer privacy-filtered diagnostic entry.

## Validation

- Release build completes with zero warnings and zero errors.
- All 19 self-tests pass, including three new LIMIT protocol regression tests.
- Live read-only NVIDIA discovery passes on an RTX 5070 Ti.

All Alpha.11 features remain available. Hardware and undocumented NVIDIA JPAC
support are still compatibility-gated at runtime. For a clean first test,
reset UV/OC and fully close MSI Afterburner or other GPU clock-control tools.
RTSS may remain running without a competing frame limiter.

The source code is planned for the public repository under MPL-2.0. This
portable build remains a binary Community Preview under the included terms.
