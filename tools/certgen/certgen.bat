"C:\Program Files\Git\usr\bin\openssl.exe" genrsa -passout pass:certPass -out ca-secret.key 4096
"C:\Program Files\Git\usr\bin\openssl.exe" rsa -passin pass:certPass -in ca-secret.key -out ca.key
"C:\Program Files\Git\usr\bin\openssl.exe" req -new -x509 -days 3650 -subj '/CN=dev_ca' -key ca.key -out ca.crt
"C:\Program Files\Git\usr\bin\openssl.exe" pkcs12 -export -passout pass:certPass -inkey ca.key -in ca.crt -out ca.pfx
::server                                  
"C:\Program Files\Git\usr\bin\openssl.exe" genrsa -passout pass:certPass -out server-secret.key 4096
"C:\Program Files\Git\usr\bin\openssl.exe" rsa -passin pass:certPass -in server-secret.key -out server.key
"C:\Program Files\Git\usr\bin\openssl.exe" req -new -subj '/O=Example server/OU=Example server unit/CN=localhost/CN=127.0.0.1' -key server.key -out server.csr
"C:\Program Files\Git\usr\bin\openssl.exe" x509 -req -days 3650 -in server.csr -CA ca.crt -CAkey ca.key -set_serial 01 -out server.crt
"C:\Program Files\Git\usr\bin\openssl.exe" pkcs12 -export -passout pass:certPass -inkey server.key -in server.crt -out server.pfx