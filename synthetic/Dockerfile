# Static binary on scratch: no shell, no package manager, no libc, nothing to CVE.
# The service makes no outbound calls, so it needs no CA bundle either.
FROM golang:1.26-alpine AS build

WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY . .

# -trimpath and the ldflags keep the binary reproducible and small.
RUN CGO_ENABLED=0 GOOS=linux go build -trimpath -ldflags="-s -w" -o /out/synthetic .

FROM scratch
COPY --from=build /out/synthetic /synthetic

# Numeric uid works without /etc/passwd, which scratch does not have.
USER 65534:65534

EXPOSE 8080 50052
ENTRYPOINT ["/synthetic"]
