FROM fptuxaes/aes-dotnet8:latest

RUN if [ -f /scripts/watch-apps.sh ]; then mv /scripts/watch-apps.sh /scripts/watch-apps.sh.disabled; fi
RUN if [ -f /scripts/generate-nginx-config.sh ]; then mv /scripts/generate-nginx-config.sh /scripts/generate-nginx-config.sh.disabled; fi
RUN if [ -f /scripts/entrypoint.sh ]; then mv /scripts/entrypoint.sh /scripts/entrypoint.sh.disabled; fi

WORKDIR /apps

ENV DOTNET_RUNNING_IN_CONTAINER=true

ENTRYPOINT ["tail", "-f", "/dev/null"]


# EXPOSE 5000