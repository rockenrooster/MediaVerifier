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
  --extra-version="mediaverifier-1" \
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
  --enable-filter=scale,crop,split,asplit,concat,trim,atrim,setpts,asetpts,null,anull,format,aformat,aresample,metadata,ametadata,ssim,psnr,siti,blockdetect,blurdetect,idet,yadif,bwdif,freezedetect,blackdetect,volumedetect,ebur128,astats,adeclip,adeclick,select,fps,pad,transpose,hflip,vflip,rotate,showinfo \
  || { cat ffbuild/config.log; exit 1; }

make -j"$(nproc)"
make install
