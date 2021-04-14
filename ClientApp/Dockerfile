FROM node:alpine as build-step
WORKDIR /app
COPY . .
RUN npm ci && npm run build:prod

FROM nginx:alpine
COPY --from=build-step /app/dist/m3u-split /usr/share/nginx/html
EXPOSE 80
