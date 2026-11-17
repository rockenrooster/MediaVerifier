#!/bin/bash
set -euo pipefail
cd /ffbuild/ffmpeg

./configure \
  --prefix=/ffbuild/prefix \
  --pkg-config-flags="--static" \
  $FFBUILD_TARGET_FLAGS \
  --cc="$CC" --cxx="$CXX" --ar="$AR" --ranlib="$RANLIB" --nm="$NM" \
  --extra-cflags="$FF_CFLAGS" --extra-cxxflags="$FF_CXXFLAGS" \
  --extra-ldflags="$FF_LDFLAGS" --extra-ldexeflags="$FF_LDEXEFLAGS" --extra-libs="$FF_LIBS" \
  --extra-version="mediaverifier-${MV_REVISION:-1}" \
  --enable-shared --disable-static \
  --disable-debug --disable-doc --disable-ffplay --disable-avdevice \
  --disable-autodetect --disable-network --disable-everything \
  --enable-w32threads --enable-zlib --enable-libdav1d --enable-libjxl \
  --enable-protocol=file,pipe \
  --enable-decoder='*' \
  --enable-demuxer='*' \
  --enable-parser='*' \
  --enable-bsf='*' \
  --enable-muxer='*' \
  --enable-encoder=wrapped_avframe,flac,alac,pcm_s16le,pcm_s24le,pcm_s32le,pcm_f32le,pcm_s16be,pcm_s24be,pcm_s32be,png,mjpeg,bmp \
  --enable-filter=scale,crop,split,asplit,concat,trim,atrim,setpts,asetpts,null,anull,format,aformat,aresample,metadata,ametadata,ssim,psnr,siti,blockdetect,blurdetect,idet,yadif,bwdif,freezedetect,blackdetect,volumedetect,ebur128,astats,adeclip,adeclick,volume,select,fps,pad,setsar,tile,hstack,transpose,hflip,vflip,rotate,showinfo,fsync \
  || { cat ffbuild/config.log; exit 1; }
# fsync is never used by the app: enabling it is what makes configure link libavfilter against libavformat, which
# the metadata filters need for their file= output (configure gives them an avformat dependency but no link to it).

make -j"$(nproc)"
make install

# Task FFB-07: the external packages linked into the DLLs and their versions, as pkg-config resolves them in this
# image (the same pkg-config configure used). deps/ffmpeg/third_party.py turns the list into THIRD-PARTY.txt.
pc=$(command -v "${FFBUILD_CROSS_PREFIX:-x86_64-w64-mingw32-}pkg-config" || command -v pkg-config)
declare -A seen=()
queue=(dav1d libjxl zlib)
: > /ffbuild/linked-packages.txt
while ((${#queue[@]})); do
  p=${queue[0]}
  queue=("${queue[@]:1}")
  [[ -n ${seen[$p]:-} ]] && continue
  seen[$p]=1
  echo "$p $("$pc" --modversion "$p")" >> /ffbuild/linked-packages.txt
  while read -r name _; do
    [[ -n $name ]] && queue+=("$name")
  done < <("$pc" --print-requires "$p"; "$pc" --print-requires-private "$p")
done
cat /ffbuild/linked-packages.txt
