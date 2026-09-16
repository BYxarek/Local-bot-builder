# Возможности Telegram Adapter

Адаптер синхронизирован с Telegram Bot API 10.3 от 24 августа 2026 года. Единый каталог `TelegramFeatures` описывает события, действия, методы Bot API, допустимые типы чатов и необходимые права.

## События и allowed_updates

При запуске бота Agent просматривает опубликованные сценарии и передаёт в `setWebhook` только нужные `allowed_updates`. Старые блоки «Команда /start», «Текстовое сообщение» и «Сообщение в группе» автоматически требуют `message`.

Поддерживаются все update-типы Bot API 10.3, включая Business, Guest Mode, реакции, inline, платежи, Managed Bots, подписки и остановку потоковой генерации. Update преобразуется в `TelegramEvent`; общие данные доступны через переменные:

- `event.update.id` и `event.update.type`;
- `event.message.text`, `event.message.id`, `event.chat.id`, `event.chat.type`;
- `event.start.payload` для нагрузки `/start` deep link;
- `event.business.connection_id`;
- `event.guest.query_id`, `event.guest.caller_user_id`, `event.guest.caller_chat_id`.

## Сообщения и контексты

Каталог содержит отправку стандартных типов контента, альбомов, опросов, игр и Rich Messages, управление сообщениями, inline-ответы, Mini Apps, Guest Mode, ephemeral-сообщения, Managed Bots, Payments и Stars.

Rich Message хранится в параметре `richMessage` как JSON-объект, а не готовая Markdown-строка. Streaming представлен методами `sendMessageDraft` и `sendRichMessageDraft`; завершённый результат должен быть отправлен обычным постоянным сообщением. Ephemeral-сообщение требует конкретного `receiverUserId`.

Community-события (`community_chat_added`, `community_chat_removed`, `community_chat_joined`) приходят как содержимое обычного `message` и сохраняются в исходной структуре сообщения. Контексты темы, Business, Guest Mode и ephemeral также сохраняются при разборе Update.

## Платежи и bot-to-bot

`pre_checkout_query` — только запрос проверки, а не подтверждение оплаты. Успешная оплата определяется полем `successful_payment` сообщения. Для цифровых товаров валидатор требует валюту `XTR`; возвраты Stars выполняются отдельным методом `refundStarPayment`.

Bot-to-bot контекст содержит `execution_id`, `origin_bot_id`, `depth`, `deduplication_key` и `timeout`. Максимальная глубина цепочки — 8; следующий переход с большей глубиной отклоняется.

## Проверка сценария

Перед публикацией проверяются известность Telegram-функции, совместимость с типом чата, права, структурность Rich Message, получатель ephemeral-сообщения и валюта цифрового товара. Privacy Mode по-прежнему проверяется для блоков чтения обычных сообщений группы.
