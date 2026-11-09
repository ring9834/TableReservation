import type { NextConfig } from "next";

// Static export only (CLAUDE.md): served as plain files from S3 + CloudFront, no server features.
const nextConfig: NextConfig = {
  output: "export",
  trailingSlash: true,
  images: { unoptimized: true },
};

export default nextConfig;
