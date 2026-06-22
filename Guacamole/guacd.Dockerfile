FROM guacamole/guacd:latest

# guacd runs as "daemon" whose home is /sbin (not writable).
# FreeRDP needs a writable home directory to store TLS certificates and
# NLA credential caches - without it, security negotiation can fail.
# Create a proper writable home and point the daemon user to it.
USER root
RUN mkdir -p /home/guacd && \
    chown daemon:daemon /home/guacd && \
    sed -i 's|daemon:x:2:2:daemon:/sbin:/sbin/nologin|daemon:x:2:2:daemon:/home/guacd:/sbin/nologin|' /etc/passwd

# Install Kerberos libs for GSSAPI/NTLM NLA support
RUN apk add --no-cache krb5-libs 2>/dev/null || true

USER daemon
