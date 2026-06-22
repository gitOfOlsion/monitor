FROM guacamole/guacd:latest

# guacd is Alpine-based. Install krb5-libs for GSSAPI/Kerberos support.
# NOTE: Due to FreeRDP 2.x limitations in this image, true Kerberos NLA may
# still not work. The real solution is security=any in connection params,
# which lets FreeRDP fall back to NTLM-based NLA (which works without krb5).
USER root
RUN apk add --no-cache krb5-libs 2>/dev/null || true
USER daemon
