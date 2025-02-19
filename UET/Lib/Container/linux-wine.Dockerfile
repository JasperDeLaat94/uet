FROM ghcr.io/redpointgames/uet/wine:9.0.0

ARG UET_TARGET_FRAMEWORK

USER root

WORKDIR /srv

ADD UET/uet/bin/Release/${UET_TARGET_FRAMEWORK}/linux-x64/publish/uet /usr/bin/uet
RUN chmod a+x /usr/bin/uet

ENTRYPOINT [ "/usr/bin/uet" ]