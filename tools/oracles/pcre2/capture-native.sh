#!/bin/sh
# Native-reference I/O wrapper: forward unchanged native CLI arguments, preserve
# testtry before upstream RunTest removes it. No project argument parsing.
"$GREGSEX_NATIVE_DRIVER" "$@"
status=$?
if [ -f testtry ]; then cp testtry "$GREGSEX_NATIVE_CAPTURE" || exit 74; fi
exit "$status"
