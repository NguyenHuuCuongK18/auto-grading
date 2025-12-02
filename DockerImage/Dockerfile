FROM fptuxaes/aes-dotnet8:latest

# Install procps package for pkill command (needed for process cleanup between test cases)
# Without pkill, the "Address already in use" error occurs when restarting applications
RUN apt-get update && apt-get install -y procps && rm -rf /var/lib/apt/lists/*

RUN if [ -f /scripts/watch-apps.sh ]; then mv /scripts/watch-apps.sh /scripts/watch-apps.sh.disabled; fi
RUN if [ -f /scripts/generate-nginx-config.sh ]; then mv /scripts/generate-nginx-config.sh /scripts/generate-nginx-config.sh.disabled; fi
RUN if [ -f /scripts/entrypoint.sh ]; then mv /scripts/entrypoint.sh /scripts/entrypoint.sh.disabled; fi

WORKDIR /apps

ENV DOTNET_RUNNING_IN_CONTAINER=true

ENTRYPOINT ["tail", "-f", "/dev/null"]


# EXPOSE 5000