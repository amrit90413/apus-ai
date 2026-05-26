/** @type {import('next').NextConfig} */
const nextConfig = {
  output: "standalone",
  async rewrites() {
    // Proxy /api → gateway so the browser never sees cross-origin or provider keys.
    return [{ source: "/api/:path*", destination: `${process.env.GATEWAY_URL ?? "http://gateway-api:8080"}/api/:path*` }];
  },
};
export default nextConfig;
