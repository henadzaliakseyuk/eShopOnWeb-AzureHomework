using System.Linq;
using System.Threading.Tasks;
using Ardalis.GuardClauses;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Mediator;
using Microsoft.eShopWeb.ApplicationCore.Entities;
using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate.Events;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.ApplicationCore.Specifications;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Microsoft.eShopWeb.ApplicationCore.Services;

public class OrderService : IOrderService
{
    private readonly IRepository<Order> _orderRepository;
    private readonly IUriComposer _uriComposer;
    private readonly IRepository<Basket> _basketRepository;
    private readonly IRepository<CatalogItem> _itemRepository;
    private readonly IMediator _mediator;

    private readonly IConfiguration _configuration;

    public OrderService(
        IRepository<Basket> basketRepository,
        IRepository<CatalogItem> itemRepository,
        IRepository<Order> orderRepository,
        IUriComposer uriComposer, 
        IMediator mediator,
        IConfiguration configuration)
    {
        _orderRepository = orderRepository;
        _uriComposer = uriComposer;
        _basketRepository = basketRepository;
        _itemRepository = itemRepository;
        _mediator = mediator;
        _configuration = configuration;
    }

    public async Task CreateOrderAsync(int basketId, Address shippingAddress)
    {
        var basketSpec = new BasketWithItemsSpecification(basketId);
        var basket = await _basketRepository.FirstOrDefaultAsync(basketSpec);

        Guard.Against.Null(basket, nameof(basket));
        Guard.Against.EmptyBasketOnCheckout(basket.Items);

        var catalogItemsSpecification = new CatalogItemsSpecification(basket.Items.Select(item => item.CatalogItemId).ToArray());
        var catalogItems = await _itemRepository.ListAsync(catalogItemsSpecification);

        var items = basket.Items.Select(basketItem =>
        {
            var catalogItem = catalogItems.First(c => c.Id == basketItem.CatalogItemId);
            var itemOrdered = new CatalogItemOrdered(catalogItem.Id, catalogItem.Name, _uriComposer.ComposePicUri(catalogItem.PictureUri));
            var orderItem = new OrderItem(itemOrdered, basketItem.UnitPrice, basketItem.Quantity);
            return orderItem;
        }).ToList();

        var order = new Order(basket.BuyerId, shippingAddress, items);

        await _orderRepository.AddAsync(order);

        var payload = new
        {
            orderId = order.Id,

            finalPrice = items.Sum(i => i.UnitPrice * i.Units),

            shippingAddress = new
            {
                street = shippingAddress.Street,
                city = shippingAddress.City,
                state = shippingAddress.State,
                country = shippingAddress.Country,
                zipCode = shippingAddress.ZipCode
            },

            items = basket.Items.Select(i => new
            {
                itemId = i.CatalogItemId,
                quantity = i.Quantity
            })
        };

        var json = JsonSerializer.Serialize(payload);

        string serviceBusConnection = _configuration["ServiceBusConnection"]!;

        await using var client =
            new ServiceBusClient(serviceBusConnection);

        ServiceBusSender sender =
            client.CreateSender("orders-queue");

        var message =
            new ServiceBusMessage(json);

        await sender.SendMessageAsync(message);

        OrderCreatedEvent orderCreatedEvent = new OrderCreatedEvent(order);
        await _mediator.Publish(orderCreatedEvent);
    }
}
