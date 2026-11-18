# -*- coding: utf-8 -*-
"""把绿色版产物目录打成 zip（保留中文文件名，UTF-8 标志位）。"""
import os
import sys
import time
import zipfile

SRC = sys.argv[1] if len(sys.argv) > 1 else "dist/DIP3-Desktop"
DST = sys.argv[2] if len(sys.argv) > 2 else "dist/DIP3-Desktop-win-x64.zip"

if not os.path.isdir(SRC):
    print("源目录不存在: " + SRC)
    sys.exit(1)

root_name = os.path.basename(os.path.normpath(SRC))
files = []
for dirpath, _dirnames, filenames in os.walk(SRC):
    for fn in filenames:
        full = os.path.join(dirpath, fn)
        rel = os.path.relpath(full, SRC)
        files.append((full, os.path.join(root_name, rel)))
files.sort(key=lambda x: x[1])

total = sum(os.path.getsize(f) for f, _ in files)
print("待打包 %d 个文件 · %.1f MB" % (len(files), total / 1048576.0))

t0 = time.time()
done = 0
with zipfile.ZipFile(DST, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    for full, arc in files:
        # 统一用 / 分隔，Windows 解压器也能识别
        z.write(full, arc.replace(os.sep, "/"))
        done += 1
        if done % 40 == 0:
            print("  ... %d/%d" % (done, len(files)))

size = os.path.getsize(DST)
print("完成: %s" % DST)
print("压缩后 %.1f MB (压缩率 %.0f%%) · 耗时 %.1fs"
      % (size / 1048576.0, size * 100.0 / total, time.time() - t0))
