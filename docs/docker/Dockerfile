# Jellyfin with what the Audio Gateway rclone VFS projection needs: a current rclone
# (distro rclone 1.60 lacks `listremotes --json`) and FUSE. Companion plugin install and
# rclone authentication stay operator-owned; see docs/rclone-cloud.md.
ARG JELLYFIN_VERSION=12.1
ARG RCLONE_VERSION=1.75.2

FROM rclone/rclone:${RCLONE_VERSION} AS rclone

FROM jellyfin/jellyfin:${JELLYFIN_VERSION}
RUN apt-get update \
    && apt-get install -y --no-install-recommends fuse3 \
    && rm -rf /var/lib/apt/lists/* \
    && echo user_allow_other >> /etc/fuse.conf
COPY --from=rclone /usr/local/bin/rclone /usr/local/bin/rclone
ENV SLIPMAT_RCLONE=/usr/local/bin/rclone \
    RCLONE_CONFIG=/rclone/rclone.conf
RUN rclone version | head -1
