kcp2k source snapshot
======================

Source: https://github.com/MirrorNetworking/kcp2k
Commit: 66efda6686f649838d42f078fbaabf56ac449de4
Date: 2024-10-12T14:42:28Z
Author: mischa
Message: fix: KcpServerConnection "invalid channel header" now logs address
License: MIT, see LICENSE in this directory.

Only the low-level KCP state machine files under kcp2k/kcp2k/kcp are vendored here.
Sharpest exposes them through Sharpest.Platform wrapper interfaces so the backing
implementation can be replaced later without leaking kcp2k APIs.
