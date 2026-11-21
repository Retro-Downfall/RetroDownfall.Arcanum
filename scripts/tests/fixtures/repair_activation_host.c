/* Protocol fixture for the controller's process and filesystem boundaries. */
#include <arpa/inet.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

static void serve(int client, const char *profile, int *running) {
    char request[8192] = {0};
    char body[4096] = {0};
    size_t received = 0;
    while (received + 1 < sizeof(request) && !strstr(request, "\r\n\r\n")) {
        ssize_t count = read(client, request + received, sizeof(request) - received - 1);
        if (count <= 0) return;
        received += (size_t) count;
        request[received] = '\0';
    }

    int status = 200;
    if (!strstr(request, "Authorization: Bearer activation-fixture-only-secret")) {
        status = 401;
        strcpy(body, "{\"isSuccess\":false}");
    } else if (!strncmp(request, "GET /api/meta ", 14)) {
        snprintf(body, sizeof(body),
                 "{\"isSuccess\":true,\"data\":{\"processId\":%d,\"startTime\":\"boot-%d\","
                 "\"nativeAot\":true,\"runtimeIdentifier\":\"osx-arm64\","
                 "\"grimoireDirectory\":\"%s\",\"configPath\":\"%s/arcanum.json\",\"fixtureRepaired\":%s}}",
                 getpid(), getpid(), profile, profile, REPAIRED ? "true" : "false");
    } else if (!strncmp(request, "GET /api/health ", 16)) {
        strcpy(body, "{\"isSuccess\":true,\"data\":{\"status\":0,\"components\":[{\"name\":\"Grimoire\",\"status\":0}]}}");
    } else if (!strncmp(request, "GET /api/sessions/", 18)) {
        char session[37] = {0};
        memcpy(session, request + 18, 36);
        if (RETAINED_EFFECT && strstr(request, "/tool-invocations/")) {
            snprintf(body, sizeof(body),
                     "{\"isSuccess\":true,\"data\":{\"receiptId\":\"10000000-0000-0000-0000-000000000003\","
                     "\"assistantEntryId\":\"10000000-0000-0000-0000-000000000002\",\"state\":\"dispatched\"}}");
        } else if (RETAINED_EFFECT) {
            snprintf(body, sizeof(body),
                     "{\"isSuccess\":true,\"data\":{\"id\":\"%s\",\"entryCount\":3,\"recoveryCheckpoint\":{"
                     "\"id\":\"10000000-0000-0000-0000-000000000001\",\"sessionId\":\"%s\","
                     "\"invocationId\":\"10000000-0000-0000-0000-000000000003\"}}}", session, session);
        } else {
            snprintf(body, sizeof(body), "{\"isSuccess\":true,\"data\":{\"id\":\"%s\",\"entryCount\":3}}", session);
        }
    } else if (!strncmp(request, "POST /api/server/repair-drain ", 30) && !LEGACY_DRAIN) {
        char *header = strstr(request, "\r\n\r\n") + 4;
        const char *length_header = strstr(request, "Content-Length: ");
        size_t hint = length_header ? (size_t) strtoul(length_header + 16, NULL, 10) : 0;
        size_t start = (size_t) (header - request);
        while (received < start + hint && received + 1 < sizeof(request)) {
            ssize_t count = read(client, request + received, sizeof(request) - received - 1);
            if (count <= 0) return;
            received += (size_t) count;
            request[received] = '\0';
        }
        char *closing = strrchr(header, '}');
        if (!closing) return;
        *closing = '\0';
        snprintf(body, sizeof(body), "{\"isSuccess\":true,\"data\":%s,\"drained\":true}}", header);
    } else if (!strncmp(request, "POST /api/server/quit ", 22)) {
        status = 202;
        strcpy(body, "{\"isSuccess\":true,\"data\":true}");
        *running = 0;
    } else {
        status = 404;
        strcpy(body, "{\"isSuccess\":false}");
    }

    dprintf(client, "HTTP/1.1 %d Fixture\r\nContent-Type: application/json\r\n"
                    "Content-Length: %zu\r\nConnection: close\r\n\r\n%s", status, strlen(body), body);
}

int main(void) {
    if (START_FAILURE) return 42;
    if (START_DELAY) sleep(START_DELAY);
    const char *home = getenv("ARCANUM_TEST_HOME");
    if (!home) return 2;
    char profile[1024] = {0};
    snprintf(profile, sizeof(profile), "%s/.config/arcanum", home);
    int listener = socket(AF_INET, SOCK_STREAM, 0);
    if (listener < 0) return 3;
    int enabled = 1;
    setsockopt(listener, SOL_SOCKET, SO_REUSEADDR, &enabled, sizeof(enabled));
    struct sockaddr_in address = {.sin_family = AF_INET, .sin_port = htons(FIXTURE_PORT)};
    inet_pton(AF_INET, "127.0.0.1", &address.sin_addr);
    if (bind(listener, (struct sockaddr *) &address, sizeof(address)) || listen(listener, 4)) return 4;
    int running = 1;
    while (running) {
        int client = accept(listener, NULL, NULL);
        if (client < 0) break;
        serve(client, profile, &running);
        close(client);
    }
    close(listener);
    return 0;
}
