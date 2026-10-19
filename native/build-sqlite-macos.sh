#!/bin/sh
# Builds the native SQLite library used by PST Browser on macOS: one universal library for Apple Silicon (arm64) and Intel (x86_64).
set -e
cd "$(dirname "$0")"
mkdir -p bin/osx
clang -O2 -dynamiclib -arch arm64 -arch x86_64 -mmacosx-version-min=12.0 \
  -DSQLITE_ENABLE_FTS5 -DSQLITE_THREADSAFE=1 -DSQLITE_DEFAULT_MEMSTATUS=0 -DSQLITE_DQS=0 -DSQLITE_OMIT_DEPRECATED \
  -DSQLITE_OMIT_LOAD_EXTENSION -DSQLITE_DEFAULT_WAL_SYNCHRONOUS=1 -DSQLITE_LIKE_DOESNT_MATCH_BLOBS -DSQLITE_USE_URI=0 \
  -DSQLITE_ENABLE_MATH_FUNCTIONS \
  -install_name @rpath/libe_sqlite3.dylib \
  -o bin/osx/libe_sqlite3.dylib sqlite/sqlite3.c
echo "bin/osx/libe_sqlite3.dylib"
