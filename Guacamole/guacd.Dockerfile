FROM guacamole/guacd:latest

# Install the GSSAPI/Kerberos library that FreeRDP needs for NLA authentication.
# Without this, guacd reports "Cannot load library libgssapi_krb5.so.2" and any
# Windows RDP server with UserAuthentication=1 (NLA required) refuses the connection
# with "wrong security type". This is a known missing dependency in the official
# guacd image — see https://github.com/apache/guacamole-client/issues for context.
USER root
RUN apt-get update -qq && \
    apt-get install -y --no-install-recommends libgssapi-krb5-2 && \
    rm -rf /var/lib/apt/lists/*
USER daemon
