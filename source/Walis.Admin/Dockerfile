# syntax=docker/dockerfile:1

# Base images are pinned by digest (see source/Dockerfile for how to bump them). node:24-alpine resolved 2026-10-21
FROM node:24-alpine@sha256:44fce696f38ba8c987c4db89954051000bbd05ffd8d8659ba15ed4a8602e937f AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
# build runs vue-tsc --noEmit first, so type errors block the image right here.
RUN npm run build

# nginx:1.28-alpine = 1.28.3, resolved 2026-10-21
FROM nginx:1.28-alpine@sha256:a8b39bd9cf0f83869a2162827a0caf6137ddf759d50a171451b335cecc87d236
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
RUN chown -R 101:101 /etc/nginx/conf.d /usr/share/nginx/html /var/cache/nginx /var/run
USER 101:101
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD wget -q -O /dev/null http://127.0.0.1:8080/_health || exit 1
