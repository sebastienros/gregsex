FROM debian:bookworm-slim@sha256:7c7b2c966bc9ee8cedfeef67e0e279108992c77681fa595db4a9d65c06ccc587
RUN printf 'deb-src [signed-by=/usr/share/keyrings/debian-archive-keyring.gpg] http://deb.debian.org/debian bookworm main\ndeb-src [signed-by=/usr/share/keyrings/debian-archive-keyring.gpg] http://deb.debian.org/debian-security bookworm-security main\ndeb-src [signed-by=/usr/share/keyrings/debian-archive-keyring.gpg] http://deb.debian.org/debian bookworm-updates main\n' > /etc/apt/sources.list.d/source.list
RUN apt-get update && apt-get install -y --no-install-recommends gcc make libc6-dev curl ca-certificates dpkg-dev xz-utils && mkdir /intake && cd /intake && apt-get source --download-only glibc && dpkg-source -x glibc*.dsc glibc-source
RUN cd /intake && curl --fail --location --max-time 120 -o musl-1.2.5.tar.gz https://musl.libc.org/releases/musl-1.2.5.tar.gz && tar xzf musl-1.2.5.tar.gz && cd musl-1.2.5 && ./configure --prefix=/opt/musl && make -j2 && make install
COPY oracle.c /intake/oracle.c
RUN gcc -std=c11 -O2 -Wall -Wextra -Werror -DORACLE_ID='"glibc-debian-bookworm"' /intake/oracle.c -o /intake/oracle-glibc && /opt/musl/bin/musl-gcc -static -std=c11 -O2 -Wall -Wextra -Werror -DORACLE_ID='"musl-1.2.5"' /intake/oracle.c -o /intake/oracle-musl
RUN cd /intake && (uname -a; gcc --version; ldd --version; dpkg-query -W libc6 libc6-dev gcc; sha256sum glibc*.dsc glibc*.tar.* musl-1.2.5.tar.gz oracle.c oracle-glibc oracle-musl /lib/aarch64-linux-gnu/libc.so.6 /opt/musl/lib/libc.a; cat /intake/musl-1.2.5/COPYRIGHT) > identity.txt
WORKDIR /intake
