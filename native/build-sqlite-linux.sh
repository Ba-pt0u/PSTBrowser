#!/bin/sh
# Builds the native SQLite library used by PST Browser on Linux (CI tests, CLI).
set -e
cd "$(dirname "$0")"
mkdir -p bin/linux-x64
gcc -O2 -shared -fPIC \
  -DSQLITE_ENABLE_FTS5 -DSQLITE_THREADSAFE=1 -DSQLITE_DEFAULT_MEMSTATUS=0 -DSQLITE_DQS=0 -DSQLITE_OMIT_DEPRECATED \
  -DSQLITE_OMIT_LOAD_EXTENSION -DSQLITE_DEFAULT_WAL_SYNCHRONOUS=1 -DSQLITE_LIKE_DOESNT_MATCH_BLOBS -DSQLITE_USE_URI=0 \
  -DSQLITE_ENABLE_MATH_FUNCTIONS \
  -o bin/linux-x64/libe_sqlite3.so sqlite/sqlite3.c -lpthread -lm
echo "bin/linux-x64/libe_sqlite3.so"
